// H# runtime, linked into every compiled program.
// threads, tasks, sockets and a bit of HTTP, all plain C with no allocations
// of its own beyond what the APIs need.

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <winhttp.h>
#include <ws2tcpip.h>
#include <windows.h>
typedef HANDLE rt_thread;
#define strtok_r strtok_s
#else
#include <pthread.h>
#include <unistd.h>
#include <sys/socket.h>
#include <sys/uio.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <arpa/inet.h>
#include <netdb.h>
#include <fcntl.h>
typedef pthread_t rt_thread;
typedef int SOCKET;
#define closesocket close
#define SOCKET_ERROR -1
#define INVALID_SOCKET -1
#define SD_SEND SHUT_WR
#endif

#include <stdatomic.h>
#include <ctype.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <errno.h>
#include <signal.h>
#include <time.h>

// forward declarations so ordering doesn't matter below
void rt_pool_start(void);
static int _stricmp_raw(const char *a, const char *b);
static void rt_reader_drop(long long sock);

// last error a line read failed with, so the timeout wrapper can tell a
// timeout apart from a dead connection
#ifdef _WIN32
#define RT_SOCK_TIMEOUT WSAETIMEDOUT
#else
#define RT_SOCK_TIMEOUT EAGAIN
#endif
static int rt_line_err;

// ---- lifecycle ----

static int rt_ready = 0;

void rt_init(void);
static int rt_exiting_flag;

#ifdef _WIN32
static BOOL WINAPI rt_ctrl(DWORD kind)
{
    (void)kind;
    rt_exiting_flag = 1;
    return TRUE;
}
#else
static void rt_sig(int sig)
{
    (void)sig;
    rt_exiting_flag = 1;
}
#endif

int rt_exiting(void)
{
    return rt_exiting_flag;
}

void rt_msleep(int ms)
{
#ifdef _WIN32
    Sleep((DWORD)ms);
#else
    struct timespec ts;
    ts.tv_sec = ms / 1000;
    ts.tv_nsec = (long)(ms % 1000) * 1000000L;
    nanosleep(&ts, NULL);
#endif
}

// per-variable mutex registry for the lock statement: one mutex per address
typedef struct rt_lockent rt_lockent;
struct rt_lockent
{
    void *key;
    rt_lockent *next;
    void *mutex;
};

static rt_lockent *rt_locks;
#ifdef _WIN32
static SRWLOCK rt_locks_guard = SRWLOCK_INIT;
#define RT_LG_ACQ AcquireSRWLockExclusive(&rt_locks_guard)
#define RT_LG_REL ReleaseSRWLockExclusive(&rt_locks_guard)
#else
static pthread_mutex_t rt_locks_guard = PTHREAD_MUTEX_INITIALIZER;
#define RT_LG_ACQ pthread_mutex_lock(&rt_locks_guard)
#define RT_LG_REL pthread_mutex_unlock(&rt_locks_guard)
#endif

// returns the mutex for key, creating it on first use. the insert re-checks
// under the guard so two threads racing the first lock share one mutex.
// never returns NULL for a valid key: allocation failure reuses a single
// process-wide mutex so locking still works, just coarser
static void *rt_lock_for(void *key)
{
    void *m = NULL;

    RT_LG_ACQ;
    for (rt_lockent *e = rt_locks; e; e = e->next)
    {
        if (e->key == key)
        {
            m = e->mutex;
            break;
        }
    }
    RT_LG_REL;

    if (m) return m;

    rt_lockent *entry = calloc(1, sizeof(rt_lockent));
    void *created = NULL;
    if (entry)
    {
#ifdef _WIN32
        created = calloc(1, sizeof(CRITICAL_SECTION));
        if (created) InitializeCriticalSection((LPCRITICAL_SECTION)created);
#else
        created = calloc(1, sizeof(pthread_mutex_t));
        if (created) pthread_mutex_init((pthread_mutex_t *)created, NULL);
#endif
    }

    RT_LG_ACQ;
    for (rt_lockent *e = rt_locks; e; e = e->next)
    {
        if (e->key == key)
        {
            m = e->mutex;
            break;
        }
    }
    if (!m)
    {
        m = created ? created : (void *)&rt_locks_guard;
        if (entry)
        {
            entry->key = key;
            entry->mutex = m;
            entry->next = rt_locks;
            rt_locks = entry;
        }
    }
    RT_LG_REL;

    // lost the race: dispose the duplicate
    if (m != created && created)
    {
#ifdef _WIN32
        DeleteCriticalSection((LPCRITICAL_SECTION)created);
#else
        pthread_mutex_destroy((pthread_mutex_t *)created);
#endif
        free(created);
        free(entry);
    }
    return m;
}

void rt_lock_acquire(void *key)
{
    void *m = rt_lock_for(key);
#ifdef _WIN32
    if (m == (void *)&rt_locks_guard) RT_LG_ACQ;
    else EnterCriticalSection((LPCRITICAL_SECTION)m);
#else
    if (m == (void *)&rt_locks_guard) RT_LG_ACQ;
    else pthread_mutex_lock((pthread_mutex_t *)m);
#endif
}

void rt_lock_release(void *key)
{
    void *m = rt_lock_for(key);
#ifdef _WIN32
    if (m == (void *)&rt_locks_guard) RT_LG_REL;
    else LeaveCriticalSection((LPCRITICAL_SECTION)m);
#else
    if (m == (void *)&rt_locks_guard) RT_LG_REL;
    else pthread_mutex_unlock((pthread_mutex_t *)m);
#endif
}

void rt_init(void)
{
    if (rt_ready) return;
    rt_exiting_flag = 0;
#ifdef _WIN32
    SetConsoleCtrlHandler(rt_ctrl, TRUE);
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
#else
    signal(SIGINT, rt_sig);
    signal(SIGTERM, rt_sig);
#endif
    rt_pool_start();
    rt_ready = 1;
}

// ---- atomic live-allocation counter (backs mem()) ----

static _Atomic long rt_live = 0;

void rt_live_inc(void) { atomic_fetch_add(&rt_live, 1); }
void rt_live_dec(void) { atomic_fetch_sub(&rt_live, 1); }
long rt_live_get(void) { return atomic_load(&rt_live); }

// ---- error messages (back catch (e)) ----
//
// one message per thread, set by whatever failed; the language's error flag
// says that something failed, this says why

#ifdef _WIN32
#define RT_TLS __declspec(thread)
#else
#define RT_TLS _Thread_local
#endif

static RT_TLS char rt_err_msg[256];

void rt_error_set(const char *msg)
{
    if (!msg) msg = "error";
    snprintf(rt_err_msg, sizeof(rt_err_msg), "%s", msg);
}

const char *rt_error_get(void)
{
    if (rt_err_msg[0] == 0) return "error";
    return rt_err_msg;
}

// ---- thread pool ----

typedef struct rt_job
{
    void (*fn)(void *);
    void *arg;
    struct rt_job *next;
} rt_job;

typedef struct
{
    rt_job *head;
    rt_job *tail;
#ifdef _WIN32
    CRITICAL_SECTION lock;
    CONDITION_VARIABLE work_avail;
#else
    pthread_mutex_t lock;
    pthread_cond_t work_avail;
#endif
} rt_queue;

static rt_queue rt_q;
static int rt_workers = 0;
static atomic_int rt_pending;  // submitted jobs not yet finished

// runs at process exit: gives detached (never-joined) tasks a bounded grace
// period so main returning does not yank workers out from under them
static void rt_pool_drain(void)
{
    int waited = 0;
#ifdef _WIN32
    while (atomic_load(&rt_pending) > 0 && waited < 3000) { Sleep(1); waited++; }
#else
    while (atomic_load(&rt_pending) > 0 && waited < 3000) { usleep(1000); waited++; }
#endif
}

static void rt_q_init(rt_queue *q)
{
    q->head = q->tail = NULL;
#ifdef _WIN32
    InitializeCriticalSection(&q->lock);
    InitializeConditionVariable(&q->work_avail);
#else
    pthread_mutex_init(&q->lock, NULL);
    pthread_cond_init(&q->work_avail, NULL);
#endif
}

static void rt_q_push(rt_queue *q, void (*fn)(void *), void *arg)
{
    rt_job *j = malloc(sizeof(rt_job));
    j->fn = fn;
    j->arg = arg;
    j->next = NULL;

#ifdef _WIN32
    EnterCriticalSection(&q->lock);
#else
    pthread_mutex_lock(&q->lock);
#endif
    if (q->tail) q->tail->next = j;
    else q->head = j;
    q->tail = j;
#ifdef _WIN32
    LeaveCriticalSection(&q->lock);
    WakeConditionVariable(&q->work_avail);
#else
    pthread_mutex_unlock(&q->lock);
    pthread_cond_signal(&q->work_avail);
#endif
}

// blocks until a job shows up; returns 0, or 1 if the worker should exit
static int rt_q_pop(rt_queue *q, void (**fn)(void *), void **arg)
{
#ifdef _WIN32
    EnterCriticalSection(&q->lock);
    while (!q->head) SleepConditionVariableCS(&q->work_avail, &q->lock, INFINITE);
#else
    pthread_mutex_lock(&q->lock);
    while (!q->head) pthread_cond_wait(&q->work_avail, &q->lock);
#endif
    rt_job *j = q->head;
    q->head = j->next;
    if (!q->head) q->tail = NULL;
#ifdef _WIN32
    LeaveCriticalSection(&q->lock);
#else
    pthread_mutex_unlock(&q->lock);
#endif
    *fn = j->fn;
    *arg = j->arg;
    free(j);
    return 0;
}

#ifdef _WIN32
static DWORD WINAPI rt_worker(LPVOID unused)
#else
static void *rt_worker(void *unused)
#endif
{
    for (;;)
    {
        void (*fn)(void *);
        void *arg;
        rt_q_pop(&rt_q, &fn, &arg);
        fn(arg);
        atomic_fetch_sub(&rt_pending, 1);
    }
#ifdef _WIN32
    return 0;
#else
    return NULL;
#endif
}

void rt_pool_start(void)
{
    if (rt_workers) return;
    atexit(rt_pool_drain);
    rt_q_init(&rt_q);

    int n = 4;
#ifdef _WIN32
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    n = (int)si.dwNumberOfProcessors;
#else
    n = (int)sysconf(_SC_NPROCESSORS_ONLN);
#endif
    if (n < 2) n = 2;
    if (n > 16) n = 16;

    for (int i = 0; i < n; i++)
    {
        rt_thread t;
#ifdef _WIN32
        t = CreateThread(NULL, 0, rt_worker, NULL, 0, NULL);
#else
        pthread_create(&t, NULL, rt_worker, NULL);
        pthread_detach(t);
#endif
    }
    rt_workers = n;
}

void rt_pool_submit(void (*fn)(void *), void *arg)
{
    rt_pool_start();
    atomic_fetch_add(&rt_pending, 1);
    rt_q_push(&rt_q, fn, arg);
}

// ---- tasks ----

typedef void *(*rt_body)(void *env);

typedef struct rt_task
{
    rt_body fn;
    void *env;
    void *result;
    int done;
    int forget;       // no one will join: the pool cleans up after the run
    int free_result;  // the boxed result is heap memory the run should free
#ifdef _WIN32
    CRITICAL_SECTION lock;
    CONDITION_VARIABLE finished;
#else
    pthread_mutex_t lock;
    pthread_cond_t finished;
#endif
} rt_task;

rt_task *rt_task_new(rt_body fn, void *env)
{
    rt_task *t = malloc(sizeof(rt_task));
    t->fn = fn;
    t->env = env;
    t->result = NULL;
    t->done = 0;
    t->forget = 0;
    t->free_result = 0;
#ifdef _WIN32
    InitializeCriticalSection(&t->lock);
    InitializeConditionVariable(&t->finished);
#else
    pthread_mutex_init(&t->lock, NULL);
    pthread_cond_init(&t->finished, NULL);
#endif
    return t;
}

static void rt_task_dispose(rt_task *t)
{
#ifdef _WIN32
    DeleteCriticalSection(&t->lock);
#else
    pthread_mutex_destroy(&t->lock);
#endif
    free(t);
}

static void rt_task_run(void *p)
{
    rt_task *t = p;
    void *r = t->fn(t->env);

#ifdef _WIN32
    EnterCriticalSection(&t->lock);
#else
    pthread_mutex_lock(&t->lock);
#endif
    t->result = r;
    t->done = 1;
#ifdef _WIN32
    LeaveCriticalSection(&t->lock);
    WakeConditionVariable(&t->finished);
#else
    pthread_mutex_unlock(&t->lock);
    pthread_cond_broadcast(&t->finished);
#endif

    // a discarded task has no joiner waiting: clean up here. the boxed
    // result is only heap when the discard site said so (owned result
    // types); int puns must not be freed
    if (t->forget)
    {
        if (t->free_result && r)
        {
            free(r);
            rt_live_dec();
        }
        rt_task_dispose(t);
    }
}

void rt_task_submit(rt_task *t)
{
    rt_pool_submit(rt_task_run, t);
}

// ---- raw threads ----
//
// accept loops must not sit on pool threads: each blocked accept would eat
// one worker for the process lifetime. these threads are detached and run
// until their function returns

#ifdef _WIN32
static DWORD WINAPI rt_spawn_trampoline(LPVOID p)
#else
static void *rt_spawn_trampoline(void *p)
#endif
{
    void **pair = p;
    void (*fn)(void *) = (void (*)(void *))pair[0];
    void *arg = pair[1];
    fn(arg);
    free(pair);
    return 0;
}

void rt_spawn(void (*fn)(void *), void *arg)
{
    void **pair = malloc(2 * sizeof(void *));
    if (!pair) return;
    pair[0] = (void *)fn;
    pair[1] = arg;
#ifdef _WIN32
    HANDLE h = CreateThread(NULL, 0, rt_spawn_trampoline, pair, 0, NULL);
    if (h) CloseHandle(h);
#else
    pthread_t t;
    if (pthread_create(&t, NULL, rt_spawn_trampoline, pair) == 0)
        pthread_detach(t);
#endif
}

// submit a task whose result nobody will collect
void rt_task_forget(rt_task *t, int free_result)
{
    t->forget = 1;
    t->free_result = free_result;
    rt_pool_submit(rt_task_run, t);
}

// blocks the calling thread until the task finished
void *rt_task_join(rt_task *t)
{
#ifdef _WIN32
    EnterCriticalSection(&t->lock);
    while (!t->done) SleepConditionVariableCS(&t->finished, &t->lock, INFINITE);
    LeaveCriticalSection(&t->lock);
#else
    pthread_mutex_lock(&t->lock);
    while (!t->done) pthread_cond_wait(&t->finished, &t->lock);
    pthread_mutex_unlock(&t->lock);
#endif
    void *r = t->result;
    free(t);
    return r;
}

int rt_task_done(rt_task *t)
{
    return t->done;
}

// the delay body's env is the millisecond count itself, not a pointer, so
// nothing needs freeing
static void *rt_delay_body(void *env)
{
    rt_msleep((int)(intptr_t)env);
    return NULL;
}

rt_task *rt_task_delay(int ms)
{
    rt_task *t = rt_task_new(rt_delay_body, (void *)(intptr_t)ms);
    rt_task_submit(t);
    return t;
}

// ---- sockets ----

static long long rt_sock_err(void)
{
#ifdef _WIN32
    return -WSAGetLastError();
#else
    return -1;
#endif
}

// best-effort windows loopback accelerator; engages only when both ends of
// a connection set it before connecting. 0x98000010 is SIO_LOOPBACK_FAST_PATH.
// disabled for now: it requires non-wildcard listener binds and showed
// intermittent stalls on this setup
static void rt_loopback_hint(SOCKET s)
{
    (void)s;
#ifdef _WIN32
    // int one = 1;
    // DWORD dummy = 0;
    // WSAIoctl(s, 0x98000010, &one, sizeof(one), NULL, 0, &dummy, NULL, NULL);
#endif
}

long long rt_tcp_listen(int port)
{
    long long s = (long long)socket(AF_INET, SOCK_STREAM, 0);
    if (s == INVALID_SOCKET) return rt_sock_err();

    int one = 1;
    setsockopt((SOCKET)s, SOL_SOCKET, SO_REUSEADDR, (const char *)&one, sizeof(one));
    rt_loopback_hint((SOCKET)s);

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = INADDR_ANY;
    addr.sin_port = htons((unsigned short)port);

    if (bind((SOCKET)s, (struct sockaddr *)&addr, sizeof(addr)) != 0)
    {
        long long e = rt_sock_err();
        closesocket((SOCKET)s);
        return e;
    }
    if (listen((SOCKET)s, SOMAXCONN) != 0)
    {
        long long e = rt_sock_err();
        closesocket((SOCKET)s);
        return e;
    }
    return s;
}

long long rt_tcp_accept(long listener)
{
    struct sockaddr_in addr;
#ifdef _WIN32
    int len = sizeof(addr);
#else
    socklen_t len = sizeof(addr);
#endif
    SOCKET c = accept((SOCKET)listener, (struct sockaddr *)&addr, &len);
    if (c == INVALID_SOCKET) return rt_sock_err();
    return c;
}

long long rt_tcp_connect(const char *host, int port)
{
    char portstr[16];
    snprintf(portstr, sizeof(portstr), "%d", port);

    struct addrinfo hints, *res = NULL;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;

    if (getaddrinfo(host, portstr, &hints, &res) != 0 || !res) return rt_sock_err();

    SOCKET s = socket(res->ai_family, res->ai_socktype, res->ai_protocol);
    rt_loopback_hint(s);
    if (s == INVALID_SOCKET)
    {
        freeaddrinfo(res);
        return rt_sock_err();
    }
    if (connect(s, res->ai_addr, (int)res->ai_addrlen) != 0)
    {
        long long e = rt_sock_err();
        closesocket(s);
        freeaddrinfo(res);
        return e;
    }
    freeaddrinfo(res);
    return s;
}

long long rt_tcp_send(long sock, const char *s, long long len)
{
    long total = 0;
    while (total < len)
    {
        long n = send((SOCKET)sock, s + total, (int)(len - total), 0);
        if (n <= 0) return rt_sock_err();
        total += n;
    }
    return total;
}

// returns bytes read, 0 on orderly close, negative on error
long long rt_tcp_recv(long sock, char *buf, long long cap)
{
    long n = recv((SOCKET)sock, buf, (int)cap, 0);
    if (n == 0) return 0;
    if (n < 0) return rt_sock_err();
    return n;
}

void rt_tcp_close(long sock)
{
    rt_reader_drop(sock);
    closesocket((SOCKET)sock);
}

long long rt_udp_open(void)
{
    SOCKET s = socket(AF_INET, SOCK_DGRAM, 0);
    if (s == INVALID_SOCKET) return rt_sock_err();
    return s;
}

// a receive-ready udp socket bound to the port
long long rt_udp_listen(int port)
{
    SOCKET s = socket(AF_INET, SOCK_DGRAM, 0);
    if (s == INVALID_SOCKET) return rt_sock_err();

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = INADDR_ANY;
    addr.sin_port = htons((unsigned short)port);

    if (bind(s, (struct sockaddr *)&addr, sizeof(addr)) != 0)
    {
        long long e = rt_sock_err();
        closesocket(s);
        return e;
    }
    return (long long)s;
}

long long rt_udp_sendto(long sock, const char *host, int port, const char *s, long long len)
{
    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons((unsigned short)port);
    addr.sin_addr.s_addr = inet_addr(host);

    long n = sendto((SOCKET)sock, s, (int)len, 0, (struct sockaddr *)&addr, sizeof(addr));
    if (n < 0) return rt_sock_err();
    return n;
}

// fills buf, returns length; sender_host gets the sender's dotted ip
long long rt_udp_recvfrom(long sock, char *buf, long long cap, char *sender_host)
{
    struct sockaddr_in addr;
#ifdef _WIN32
    int len = sizeof(addr);
#else
    socklen_t len = sizeof(addr);
#endif
    long n = recvfrom((SOCKET)sock, buf, (int)cap, 0, (struct sockaddr *)&addr, &len);
    if (n < 0) return rt_sock_err();
    if (sender_host)
    {
        const char *ip = inet_ntoa(addr.sin_addr);
        snprintf(sender_host, 64, "%s", ip ? ip : "?");
    }
    return n;
}

// ---- line reads ----

// reads up to newline, strips \r\n. returns a malloc'd string (counter bumped),
// ---- buffered line reading ----
//
// rt_tcp_line used to recv one byte at a time, a syscall per character.
// each socket reading lines gets a slot that pulls 4 KiB per syscall
// instead. one reader per socket; slots clear on close so a recycled socket
// handle never sees the previous connection's bytes

#define RT_READERS 64
#define RT_READBUF 4096

typedef struct
{
    long long sock;
    int len, pos;
    char buf[RT_READBUF];
} rt_reader;

static rt_reader rt_readers[RT_READERS];

#ifdef _WIN32
static SRWLOCK rt_readers_lock = SRWLOCK_INIT;
#define RT_RD_LOCK AcquireSRWLockExclusive(&rt_readers_lock)
#define RT_RD_UNLOCK ReleaseSRWLockExclusive(&rt_readers_lock)
#else
static pthread_mutex_t rt_readers_lock = PTHREAD_MUTEX_INITIALIZER;
#define RT_RD_LOCK pthread_mutex_lock(&rt_readers_lock)
#define RT_RD_UNLOCK pthread_mutex_unlock(&rt_readers_lock)
#endif

static rt_reader *rt_reader_get(long long sock)
{
    rt_reader *free = NULL;

    RT_RD_LOCK;
    for (int i = 0; i < RT_READERS; i++)
    {
        if (rt_readers[i].sock == sock)
        {
            RT_RD_UNLOCK;
            return &rt_readers[i];
        }
        if (rt_readers[i].sock == 0 && !free) free = &rt_readers[i];
    }
    if (free)
    {
        free->sock = sock;
        free->len = 0;
        free->pos = 0;
    }
    RT_RD_UNLOCK;
    return free;   // NULL when every slot is busy, caller falls back
}

static void rt_reader_drop(long long sock)
{
    RT_RD_LOCK;
    for (int i = 0; i < RT_READERS; i++)
        if (rt_readers[i].sock == sock) rt_readers[i].sock = 0;
    RT_RD_UNLOCK;
}

// "" on orderly close, NULL on error. the caller owns the buffer
char *rt_tcp_line(long sock)
{
    char *buf = malloc(8192);
    long used = 0;
    rt_reader *rd = rt_reader_get(sock);

    while (used < 8191)
    {
        char c;

        if (rd && rd->pos >= rd->len)
        {
            long n = recv((SOCKET)sock, rd->buf, RT_READBUF, 0);
            if (n < 0)
            {
                rt_line_err =
#ifdef _WIN32
                    WSAGetLastError();
#else
                    errno;
#endif
                rt_reader_drop(sock);
                free(buf);
                return NULL;
            }
            if (n == 0) break;
            rd->len = (int)n;
            rd->pos = 0;
        }

        if (rd)
        {
            c = rd->buf[rd->pos++];
        }
        else
        {
            long n = recv((SOCKET)sock, &c, 1, 0);
            if (n < 0)
            {
                rt_line_err =
#ifdef _WIN32
                    WSAGetLastError();
#else
                    errno;
#endif
                free(buf);
                return NULL;
            }
            if (n == 0) break;
        }

        if (c == '\n') break;
        if (c != '\r') buf[used++] = c;
    }

    buf[used] = 0;
    rt_live_inc();
    return buf;
}

// same read with a one-shot receive timeout: the reply on success, NULL on
// timeout, (char *)-1 on a hard error. windows takes SO_RCVTIMEO as a DWORD
// in milliseconds, everyone else as a timeval
static void rt_set_rcvtimeo(SOCKET s, int ms)
{
#ifdef _WIN32
    DWORD msD = (DWORD)ms;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char *)&msD, sizeof(msD));
#else
    struct timeval tv;
    tv.tv_sec = ms / 1000;
    tv.tv_usec = (ms % 1000) * 1000;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char *)&tv, sizeof(tv));
#endif
}

char *rt_tcp_line_timeout(long sock, int ms)
{
    rt_set_rcvtimeo((SOCKET)sock, ms);

    rt_line_err = 0;
    char *line = rt_tcp_line(sock);

    rt_set_rcvtimeo((SOCKET)sock, 0);

    if (line) return line;
    if (rt_line_err == RT_SOCK_TIMEOUT) return NULL;
    return (char *)(intptr_t)-1;
}

// waits for one datagram, returns it as a malloc'd string (counter bumped),
// NULL on error. the sender is ignored
char *rt_udp_recv(long sock)
{
    char *buf = malloc(8192);
    long n = rt_udp_recvfrom(sock, buf, 8191, NULL);
    if (n < 0)
    {
        free(buf);
        return NULL;
    }
    buf[n] = 0;
    rt_live_inc();
    return buf;
}

// case-insensitive "name: value" lookup inside ONE header line.
// returns value length or 0, value points into line
long rt_http_header_raw(const char *line, const char *name, char **value)
{
    long namelen = (long)strlen(name);
    if (_stricmp_raw(line, name) != 0) return 0;
    if (line[namelen] != ':') return 0;

    long start = namelen + 1;
    while (line[start] == ' ') start++;

    long end = start;
    while (line[end] && line[end] != '\r' && line[end] != '\n') end++;

    *value = (char *)(line + start);
    return end - start;
}

// ---- http ----

#ifdef _WIN32
#define RT_TLS __declspec(thread)
#else
#define RT_TLS _Thread_local
#endif

static RT_TLS int rt_http_status = 0;

int rt_http_last_status(void)
{
    return rt_http_status;
}

// splits an http url into host, port and path.
// returns 0, or -1 for a bad url and -2 for https (no TLS support yet)
static int rt_url_split(const char *url, char *host, int *port, char *path, int *tls)
{
    // host and path end up in request headers, so control characters are
    // rejected outright: no header injection, no request smuggling
    for (const char *c = url; *c; c++)
    {
        unsigned char ch = (unsigned char)*c;
        if (ch < 0x20 || ch == 0x7f) return -1;
    }

    if (strncmp(url, "http://", 7) == 0)
    {
        url += 7;
        if (tls) *tls = 0;
    }
    else if (strncmp(url, "https://", 8) == 0)
    {
        url += 8;
        if (tls) *tls = 1;
#ifndef _WIN32
        return -2;
#endif
    }
    else
    {
        return -1;
    }

    const char *slash = strchr(url, '/');
    long hostlen = slash ? slash - url : (long)strlen(url);
    if (hostlen <= 0 || hostlen > 252) return -1;

    memcpy(host, url, hostlen);
    host[hostlen] = 0;

    char *colon = strchr(host, ':');
    *port = (tls && *tls) ? 443 : 80;
    if (colon)
    {
        *colon = 0;
        *port = atoi(colon + 1);
        if (*port <= 0) return -1;
    }

    snprintf(path, 1024, "%s", slash ? slash : "/");
    return 0;
}

// ssrf filter: http requests only ever leave for public addresses. loopback,
// private, link-local and reserved ranges are rejected before any connect
static int rt_v4_blocked(const unsigned char *b)
{
    if (b[0] == 127) return 1;                                  // loopback
    if (b[0] == 10) return 1;                                   // private
    if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return 1;      // private
    if (b[0] == 192 && b[1] == 168) return 1;                   // private
    if (b[0] == 169 && b[1] == 254) return 1;                   // link-local
    if (b[0] == 0 || b[0] >= 240) return 1;                     // reserved
    return 0;
}

// resolves host and picks the first address the filter allows. returns an
// IP literal in buf, or 0 when nothing about the host is safely reachable
static int rt_http_pick_host(const char *host, char *buf, int buflen)
{
    if (strcmp(host, "localhost") == 0) return 0;

    struct addrinfo hints, *res = NULL, *p;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;

    if (getaddrinfo(host, NULL, &hints, &res) != 0 || !res) return 0;

    for (p = res; p; p = p->ai_next)
    {
        if (p->ai_family != AF_INET) continue;
        struct sockaddr_in *a = (struct sockaddr_in *)p->ai_addr;
        unsigned char b[4];
        memcpy(b, &a->sin_addr.s_addr, 4);
        if (rt_v4_blocked(b)) continue;
        if (inet_ntop(AF_INET, &a->sin_addr, buf, (size_t)buflen))
        {
            freeaddrinfo(res);
            return 1;
        }
    }

    freeaddrinfo(res);
    return 0;
}

#define RT_HTTP_MAX (64 * 1024)

// appends with a hard cap; returns the new length or -1 when out of room
static long rt_append(char *dst, long len, long cap, const char *s, long n)
{
    if (n < 0 || len < 0 || cap - len - 1 < n) return -1;
    memcpy(dst + len, s, (size_t)n);
    return len + n;
}

static long rt_append_str(char *dst, long len, long cap, const char *s)
{
    return rt_append(dst, len, cap, s, (long)strlen(s));
}



// one request over a fresh connection. HTTP/1.0 so the server closes the
// stream and the body just ends at EOF. responses are capped at RT_HTTP_MAX.
// returns a malloc'd body or NULL; the status code lands in
// rt_http_last_status(): -2 = https without TLS (non-windows), -3 = address
// filter, -4 = response too large, -5 = tls handshake failed

// one request over a fresh connection, through WinHTTP: TLS, certificate
// validation and response framing are the platform's job. responses are
// capped at RT_HTTP_MAX; status lands in rt_http_last_status(): -3 =
// address filter, -4 = response too large, -5 = platform request failed
#ifdef _WIN32
static char *rt_http_req(const char *url, const char *method, const char *body)
{
    char host[256], path[1024], ip[64];
    int port = 80;
    int tls = 0;

    rt_http_status = 0;
    int split = rt_url_split(url, host, &port, path, &tls);
    if (split != 0)
    {
        rt_http_status = split;
        return NULL;
    }

    if (!rt_http_pick_host(host, ip, sizeof(ip)))
    {
        rt_http_status = -3;
        return NULL;
    }

    wchar_t whost[256], wpath[1024], wmethod[16];
    MultiByteToWideChar(CP_UTF8, 0, host, -1, whost, 256);
    MultiByteToWideChar(CP_UTF8, 0, path, -1, wpath, 1024);
    MultiByteToWideChar(CP_UTF8, 0, method, -1, wmethod, 16);

    char *out = NULL;
    HINTERNET ses = WinHttpOpen(L"hsharp", WINHTTP_ACCESS_TYPE_NO_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!ses)
    {
        rt_http_status = -5;
        return NULL;
    }
    HINTERNET con = WinHttpConnect(ses, whost, (INTERNET_PORT)port, 0);
    HINTERNET req = con ? WinHttpOpenRequest(con, wmethod, wpath, NULL, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, tls ? WINHTTP_FLAG_SECURE : 0) : NULL;
    if (!req)
    {
        rt_http_status = -5;
    }
    else
    {
        DWORD blen = body ? (DWORD)strlen(body) : 0;
        if (WinHttpSendRequest(req, L"Content-Type: text/plain\r\n", (DWORD)-1,
                body ? (LPVOID)body : WINHTTP_NO_REQUEST_DATA, blen, blen, 0)
            && WinHttpReceiveResponse(req, NULL))
        {
            DWORD code = 0, csize = sizeof(code);
            WinHttpQueryHeaders(req, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &code, &csize, WINHTTP_NO_HEADER_INDEX);
            rt_http_status = (int)code;

            char *raw = malloc(RT_HTTP_MAX);
            long len = 0;
            DWORD avail = 0, read = 0;
            while (WinHttpQueryDataAvailable(req, &avail) && avail > 0)
            {
                if (len + avail + 1 >= RT_HTTP_MAX) { rt_http_status = -4; free(raw); raw = NULL; break; }
                if (!WinHttpReadData(req, raw + len, avail, &read) || read == 0) break;
                len += read;
            }
            if (raw)
            {
                if (rt_http_status >= 200)
                {
                    raw[len] = 0;
                    out = raw;
                    rt_live_inc();
                }
                else free(raw);
            }
        }
        else rt_http_status = -5;
    }
    if (req) WinHttpCloseHandle(req);
    if (con) WinHttpCloseHandle(con);
    WinHttpCloseHandle(ses);
    return out;
}
#else
static char *rt_http_req(const char *url, const char *method, const char *body)
{
    (void)method; (void)body;
    char host[256], path[1024], ip[64];
    int port = 80;
    int tls = 0;
    rt_http_status = 0;
    int split = rt_url_split(url, host, &port, path, &tls);
    if (split != 0) { rt_http_status = split; return NULL; }
    if (!rt_http_pick_host(host, ip, sizeof(ip))) { rt_http_status = -3; return NULL; }
    rt_http_status = -2;
    return NULL;
}
#endif

char *rt_http_get(const char *url)
{
    return rt_http_req(url, "GET", NULL);
}

char *rt_http_post(const char *url, const char *body)
{
    return rt_http_req(url, "POST", body);
}

// ---- http server ----
//
// built for the raw path: accept-only. ListenRaw's Accept returns the moment
// the tcp handshake completes, with both endpoints known from the accept
// call itself, before a single payload byte is read, parsed or allocated.
// buffers come from a reuse pool so accept/drop cycles do zero malloc/free,
// and ToHttpPacket() reads and parses on demand. packets and their buffers
// are runtime-owned (like sockets), not part of the mem() counter; handles
// are pointers, so they travel as long long (long is 32-bit on windows)

typedef struct rt_http_rq
{
    long long sock;
    struct sockaddr_in peer;   // straight from accept, no getpeername
    char *buf;                 // 64 KiB, allocated on first read, pooled
    long long len;
    long long scan;            // head-end scan resume point
    long long bodyStart;       // where the body begins in buf, -1 until head is in
    long long contentLen;      // from Content-Length, -1 when absent
    int headRead;              // set once the head has been read (buf alone is
                               // not proof: pooled packets keep their buffer)
    int parsed;
    int haveSrc, haveDst;
    char method[16];
    long long pathOff, pathLen;
    char src[64];
    char dst[64];
    char hdrs[8][256];         // response headers set via Header(name, value)
    int hdrCount;
    char setck[8][512];        // Set-Cookie lines from Cookies.Set
    int setckCount;
    struct rt_http_rq *next;   // pool freelist link
} rt_http_rq;

// capped so a burst of concurrent requests cannot pin unbounded memory
#define RT_RQ_POOL_MAX 64
static rt_http_rq *rt_rq_pool;
static int rt_rq_pool_len;

#ifdef _WIN32
static SRWLOCK rt_rq_lock = SRWLOCK_INIT;
#define RT_LOCK_ACQUIRE AcquireSRWLockExclusive(&rt_rq_lock)
#define RT_LOCK_RELEASE ReleaseSRWLockExclusive(&rt_rq_lock)
#else
static pthread_mutex_t rt_rq_lock = PTHREAD_MUTEX_INITIALIZER;
#define RT_LOCK_ACQUIRE pthread_mutex_lock(&rt_rq_lock)
#define RT_LOCK_RELEASE pthread_mutex_unlock(&rt_rq_lock)
#endif

// buffer stays attached across reuse; everything else resets
static rt_http_rq *rt_rq_acquire(void)
{
    rt_http_rq *r = NULL;

    RT_LOCK_ACQUIRE;
    if (rt_rq_pool)
    {
        r = rt_rq_pool;
        rt_rq_pool = r->next;
        rt_rq_pool_len--;
    }
    RT_LOCK_RELEASE;

    if (!r)
    {
        r = calloc(1, sizeof(rt_http_rq));
        if (!r) return NULL;
    }

    r->sock = -1;
    r->len = 0;
    r->scan = 0;
    r->bodyStart = -1;
    r->contentLen = -1;
    r->headRead = 0;
    r->parsed = 0;
    r->haveSrc = 0;
    r->haveDst = 0;
    r->method[0] = 0;
    r->src[0] = 0;
    r->dst[0] = 0;
    r->hdrCount = 0;
    r->setckCount = 0;
    r->next = NULL;
    return r;
}

static void rt_rq_release(rt_http_rq *r)
{
    if (!r) return;
    if (r->sock >= 0) rt_tcp_close(r->sock);
    r->sock = -1;

    RT_LOCK_ACQUIRE;
    if (rt_rq_pool_len < RT_RQ_POOL_MAX)
    {
        r->next = rt_rq_pool;
        rt_rq_pool = r;
        rt_rq_pool_len++;
        RT_LOCK_RELEASE;
        return;
    }
    RT_LOCK_RELEASE;

    free(r->buf);
    free(r);
}

static char *rt_rq_dup(const char *s, long long n)
{
    char *out = malloc((size_t)n + 1);
    if (!out) return NULL;
    memcpy(out, s, (size_t)n);
    out[n] = 0;
    rt_live_inc();
    return out;
}

// head is in once a blank line shows up. resumes from the last scan position
// so repeated small reads do not rescan the whole buffer
static int rt_rq_head_complete(rt_http_rq *r)
{
    long long start = r->scan > 3 ? r->scan - 3 : 0;
    for (long long i = start; i + 3 < r->len; i++)
    {
        if (r->buf[i] == '\r' && r->buf[i + 1] == '\n' && r->buf[i + 2] == '\r' && r->buf[i + 3] == '\n')
        {
            r->bodyStart = i + 4;
            r->scan = r->len;
            return 1;
        }
    }
    r->scan = r->len;
    return 0;
}

static int rt_rq_read_head(rt_http_rq *r)
{
    if (!r->buf)
    {
        r->buf = malloc(RT_HTTP_MAX);
        if (!r->buf) return 0;
    }
    r->len = 0;
    r->scan = 0;

    while (!rt_rq_head_complete(r))
    {
        if (r->len + 1 >= RT_HTTP_MAX) return 0;
        long n = rt_tcp_recv(r->sock, r->buf + r->len, RT_HTTP_MAX - r->len - 1);
        if (n <= 0) return 0;
        r->len += n;
    }
    r->buf[r->len] = 0;
    r->headRead = 1;
    return 1;
}

// "Name: value" lookup inside the head. returns the value length and sets
// *off into buf, 0 when absent, -1 on a malformed line
static long long rt_rq_header(rt_http_rq *r, const char *name, long long *off)
{
    long long nameLen = (long long)strlen(name);
    long long pos = 0;

    while (pos < r->bodyStart)
    {
        long long end = pos;
        while (end < r->bodyStart && r->buf[end] != '\n') end++;
        if (end >= r->bodyStart) return -1;

        long long lineLen = end - pos;
        if (r->buf[end - 1] == '\r') lineLen--;

        if (pos > 0 && lineLen > nameLen)
        {
            int match = r->buf[pos + nameLen] == ':';
            for (long long i = 0; match && i < nameLen; i++)
            {
                char a = r->buf[pos + i], b = name[i];
                if (a >= 'A' && a <= 'Z') a += 32;
                if (b >= 'A' && b <= 'Z') b += 32;
                match = a == b;
            }
            if (match)
            {
                long long v = pos + nameLen + 1;
                while (v < pos + lineLen && r->buf[v] == ' ') v++;
                *off = v;
                return pos + lineLen - v;
            }
        }

        pos = end + 1;
    }
    return 0;
}

static int rt_rq_parse_line(rt_http_rq *r)
{
    long long sp1 = -1, sp2 = -1;
    for (long long i = 0; i < r->bodyStart; i++)
    {
        char c = r->buf[i];
        if (c == ' ' || c == '\r' || c == '\n')
        {
            if (sp1 < 0) sp1 = i;
            else { sp2 = i; break; }
        }
    }
    if (sp1 <= 0 || sp2 < 0) return 0;

    if (sp1 >= (long long)sizeof(r->method) - 1) return 0;
    memcpy(r->method, r->buf, (size_t)sp1);
    r->method[sp1] = 0;

    r->pathOff = sp1 + 1;
    r->pathLen = sp2 - r->pathOff;
    return r->pathLen > 0;
}

// pulls in the Content-Length worth of body bytes; bodies without a length
// (GET, chunked) count as empty
static int rt_rq_read_body(rt_http_rq *r)
{
    long long vOff = 0;
    long long vLen = rt_rq_header(r, "Content-Length", &vOff);
    r->contentLen = -1;

    if (vLen < 0) return 0;
    if (vLen > 0)
    {
        if (vLen >= 24) return 0;
        char tmp[24];
        memcpy(tmp, r->buf + vOff, (size_t)vLen);
        tmp[vLen] = 0;

        char *end = NULL;
        long long cl = strtol(tmp, &end, 10);
        if (end == tmp || cl < 0 || r->bodyStart + cl >= RT_HTTP_MAX) return 0;
        r->contentLen = cl;
    }

    if (r->contentLen < 0) return 1;

    while (r->len - r->bodyStart < r->contentLen)
    {
        long n = rt_tcp_recv(r->sock, r->buf + r->len, RT_HTTP_MAX - r->len - 1);
        if (n <= 0) return 0;
        r->len += n;
    }
    r->buf[r->len] = 0;
    return 1;
}

static void rt_rq_fill_src(rt_http_rq *r)
{
    if (r->haveSrc) return;
    r->haveSrc = 1;

    if (r->peer.sin_family != AF_INET) return;
    char ip[INET_ADDRSTRLEN];
    if (!inet_ntop(AF_INET, &r->peer.sin_addr, ip, sizeof(ip))) return;
    snprintf(r->src, sizeof(r->src), "%s:%d", ip, ntohs(r->peer.sin_port));
}

// the local end needs its own lookup, so it waits until someone asks
static void rt_rq_fill_dst(rt_http_rq *r)
{
    if (r->haveDst) return;
    r->haveDst = 1;

    struct sockaddr_in sa;
    int salen = sizeof(sa);
    if (getsockname((SOCKET)r->sock, (struct sockaddr *)&sa, &salen) != 0) return;
    if (sa.sin_family != AF_INET) return;

    char ip[INET_ADDRSTRLEN];
    if (!inet_ntop(AF_INET, &sa.sin_addr, ip, sizeof(ip))) return;
    snprintf(r->dst, sizeof(r->dst), "%s:%d", ip, ntohs(sa.sin_port));
}

// accepts one connection. raw = 1 returns at the handshake with both
// endpoints known and nothing read; raw = 0 also pulls the head, parses the
// request line and drains the body. -1 on any failure
long long rt_http_accept(long long listener, int raw)
{
    struct sockaddr_in peer;
    int plen = sizeof(peer);

    SOCKET s = accept((SOCKET)listener, (struct sockaddr *)&peer, &plen);
    if (s == INVALID_SOCKET) return -1;

    rt_http_rq *r = rt_rq_acquire();
    if (!r)
    {
        closesocket(s);
        return -1;
    }

    r->sock = (long long)s;
    r->peer = peer;

    // latency-bound request/response traffic, no nagle delays
    int one = 1;
    setsockopt(s, IPPROTO_TCP, TCP_NODELAY, (const char *)&one, sizeof(one));

    if (!raw)
    {
        if (!rt_rq_read_head(r) || !rt_rq_parse_line(r) || !rt_rq_read_body(r))
        {
            rt_rq_release(r);
            return -1;
        }
        r->parsed = 1;
    }
    return (long long)(intptr_t)r;
}

// lazily does what the plain listener did eagerly: read the head if this is
// a raw packet, then request line + body, in place
long long rt_http_to_packet(long long req)
{
    rt_http_rq *r = (rt_http_rq *)(intptr_t)req;
    if (!r) return -1;
    if (!r->parsed)
    {
        if (!r->headRead && !rt_rq_read_head(r)) return -1;
        if (!rt_rq_parse_line(r) || !rt_rq_read_body(r)) return -1;
        r->parsed = 1;
    }
    return req;
}

char *rt_http_method(long long req)
{
    rt_http_rq *r = (rt_http_rq *)(intptr_t)req;
    if (!r || !r->parsed) return rt_rq_dup("", 0);
    return rt_rq_dup(r->method, (long long)strlen(r->method));
}

char *rt_http_path(long long req)
{
    rt_http_rq *r = (rt_http_rq *)(intptr_t)req;
    if (!r || !r->parsed) return rt_rq_dup("", 0);
    return rt_rq_dup(r->buf + r->pathOff, r->pathLen);
}

char *rt_http_header(long long req, const char *name)
{
    rt_http_rq *r = (rt_http_rq *)(intptr_t)req;
    if (!r) return NULL;
    long long vOff = 0;
    long long vLen = rt_rq_header(r, name, &vOff);
    if (vLen <= 0) return NULL;
    return rt_rq_dup(r->buf + vOff, vLen);
}

char *rt_http_body(long long req)
{
    rt_http_rq *r = (rt_http_rq *)(intptr_t)req;
    if (!r || !r->parsed || r->contentLen <= 0) return rt_rq_dup("", 0);
    return rt_rq_dup(r->buf + r->bodyStart, r->contentLen);
}

char *rt_http_source(long long req)
{
    rt_http_rq *r = (rt_http_rq *)(intptr_t)req;
    if (!r) return rt_rq_dup("", 0);
    rt_rq_fill_src(r);
    return rt_rq_dup(r->src, (long long)strlen(r->src));
}

char *rt_http_dest(long long req)
{
    rt_http_rq *r = (rt_http_rq *)(intptr_t)req;
    if (!r) return rt_rq_dup("", 0);
    rt_rq_fill_dst(r);
    return rt_rq_dup(r->dst, (long long)strlen(r->dst));
}

static const char *rt_status_reason(int code)
{
    switch (code)
    {
        case 200: return "OK";
        case 201: return "Created";
        case 204: return "No Content";
        case 301: return "Moved Permanently";
        case 302: return "Found";
        case 400: return "Bad Request";
        case 401: return "Unauthorized";
        case 403: return "Forbidden";
        case 404: return "Not Found";
        case 405: return "Method Not Allowed";
        case 413: return "Payload Too Large";
        case 500: return "Internal Server Error";
        case 502: return "Bad Gateway";
        case 503: return "Service Unavailable";
        default: return "";
    }
}

// one syscall for head+body: small bodies get copied next to the head, big
// ones gather via writev (wsasend's gather path stalls on this setup)
static long rt_tcp_send_two(long long sock, const char *a, long long alen, const char *b, long long blen)
{
    long long total = 0;

#ifdef _WIN32
    if (alen + blen <= 16384)
    {
        char combo[16384];
        memcpy(combo, a, (size_t)alen);
        memcpy(combo + alen, b, (size_t)blen);
        return rt_tcp_send(sock, combo, alen + blen);
    }
    if (rt_tcp_send(sock, a, alen) < 0) return -1;
    return rt_tcp_send(sock, b, blen);
#else
    struct iovec iov[2];
    int n = 0;
    if (alen > 0) { iov[n].iov_base = (void *)a; iov[n].iov_len = (size_t)alen; n++; }
    if (blen > 0) { iov[n].iov_base = (void *)b; iov[n].iov_len = (size_t)blen; n++; }
    if (n == 0) return 0;

    while (total < alen + blen)
    {
        ssize_t w = writev((int)sock, iov, n);
        if (w < 0) return -1;
        total += w;
        if (total < alen + blen)
        {
            // partial: rebase the vectors onto what is left
            long long skip = total;
            int m = 0;
            if (skip < alen)
            {
                iov[m].iov_base = (void *)(a + skip);
                iov[m].iov_len = (size_t)(alen - skip);
                m++;
                iov[m].iov_base = (void *)b;
                iov[m].iov_len = (size_t)blen;
                m++;
            }
            else
            {
                iov[m].iov_base = (void *)(b + (skip - alen));
                iov[m].iov_len = (size_t)(alen + blen - skip);
                m++;
            }
            n = m;
        }
    }
#endif
    return (long)total;
}

// headers are stored as "Name: value"; this matches the stored name
// case-insensitively against a bare name
static int rt_hdr_matches(const char *stored, const char *name)
{
    while (*name)
    {
        if (tolower((unsigned char)*stored) != tolower((unsigned char)*name)) return 0;
        stored++;
        name++;
    }
    return *stored == ':';
}

// stores a response header on the packet; a value may not inject CRLF
long long rt_http_set_header(long long req, const char *name, const char *value)
{
    rt_http_rq *r = (rt_http_rq *)(intptr_t)req;
    if (!r || !name || !*name || !value) return -1;
    for (const char *p = name; *p; p++)
        if (*p == ':' || *p == '\r' || *p == '\n') return -1;
    for (const char *p = value; *p; p++)
        if (*p == '\r' || *p == '\n') return -1;

    for (int i = 0; i < r->hdrCount; i++)
    {
        if (rt_hdr_matches(r->hdrs[i], name))
        {
            snprintf(r->hdrs[i], sizeof(r->hdrs[i]), "%s: %s", name, value);
            return 0;
        }
    }
    if (r->hdrCount >= 8) return -1;
    snprintf(r->hdrs[r->hdrCount], sizeof(r->hdrs[r->hdrCount]), "%s: %s", name, value);
    r->hdrCount++;
    return 0;
}

// the cookies facade is the packet itself
long long rt_http_cookies(long long req)
{
    return req;
}

// cookie names cannot contain separators; values cannot contain CR, LF or
// semicolon (an '=' inside a value is fine)
static int rt_cookie_bad(const char *name, const char *value)
{
    if (!name || !*name || !value) return 1;
    for (const char *p = name; *p; p++)
        if (*p == ';' || *p == '=' || *p == '\r' || *p == '\n') return 1;
    for (const char *p = value; *p; p++)
        if (*p == ';' || *p == '\r' || *p == '\n') return 1;
    return 0;
}

static const char *rt_samesite_name(int s)
{
    switch (s)
    {
        case 1: return "Lax";
        case 2: return "Strict";
        default: return NULL;
    }
}

// appends a Set-Cookie header; MaxAge < 0 means a session cookie, an empty
// path omits the attribute
static long long rt_cookie_store(rt_http_rq *r, const char *line)
{
    if (r->setckCount >= 8) return -1;
    snprintf(r->setck[r->setckCount], sizeof(r->setck[r->setckCount]), "%s", line);
    r->setckCount++;
    return 0;
}

long long rt_http_cookie_set(long long req, const char *name, const char *value,
                             int secure, int httpOnly, int sameSite,
                             const char *path, const char *domain, int maxAge)
{
    rt_http_rq *r = (rt_http_rq *)(intptr_t)req;
    if (!r || rt_cookie_bad(name, value)) return -1;

    char line[512];
    long hl = 0;
    hl += snprintf(line, sizeof(line), "%s=%s", name, value);
    if (path && *path) hl += snprintf(line + hl, sizeof(line) - hl, "; Path=%s", path);
    if (domain && *domain) hl += snprintf(line + hl, sizeof(line) - hl, "; Domain=%s", domain);
    if (maxAge >= 0) hl += snprintf(line + hl, sizeof(line) - hl, "; Max-Age=%d", maxAge);
    if (secure) hl += snprintf(line + hl, sizeof(line) - hl, "; Secure");
    if (httpOnly) hl += snprintf(line + hl, sizeof(line) - hl, "; HttpOnly");
    const char *ss = rt_samesite_name(sameSite);
    if (ss) hl += snprintf(line + hl, sizeof(line) - hl, "; SameSite=%s", ss);
    if (hl < 0 || hl >= (long)sizeof(line)) return -1;
    return rt_cookie_store(r, line);
}

long long rt_http_cookie_setdef(long long req, const char *name, const char *value)
{
    // asp-style defaults: host-only session cookie on /, no flags, Lax
    return rt_http_cookie_set(req, name, value, 0, 0, 1, "/", "", -1);
}

// reads one cookie out of the request's Cookie header
char *rt_http_cookie_get(long long req, const char *name)
{
    rt_http_rq *r = (rt_http_rq *)(intptr_t)req;
    if (!r || !name || !*name) return NULL;

    char *hdr = rt_http_header(req, "Cookie");
    if (!hdr) return NULL;

    size_t nlen = strlen(name);
    char *out = NULL;
    char *save = NULL;
    for (char *pair = strtok_r(hdr, ";", &save); pair; pair = strtok_r(NULL, ";", &save))
    {
        while (*pair == ' ') pair++;
        char *eq = strchr(pair, '=');
        if (!eq) continue;
        *eq = 0;
        char *v = eq + 1;
        size_t plen = strlen(pair);
        while (plen > 0 && pair[plen - 1] == ' ') pair[--plen] = 0;
        int match = plen == nlen;
        for (size_t k = 0; match && k < plen; k++)
            if (tolower((unsigned char)pair[k]) != tolower((unsigned char)name[k])) match = 0;
        if (match)
        {
            out = rt_rq_dup(v, strlen(v));
            break;
        }
    }
    free(hdr);
    rt_live_dec();
    return out;
}

// sends the response in one gather-write and closes the request out; single
// use, like the HTTP/1.0 connection model the client speaks
long long rt_http_respond(long long req, int status, const char *body)
{
    rt_http_rq *r = (rt_http_rq *)(intptr_t)req;
    if (!r || !body) return -1;

    long long blen = (long long)strlen(body);
    char head[2048];
    long hl = 0;
    char num[24];

    int ctypeSet = 0;
    for (int i = 0; i < r->hdrCount; i++)
        if (rt_hdr_matches(r->hdrs[i], "Content-Type")) ctypeSet = 1;

    hl = rt_append_str(head, hl, sizeof(head), "HTTP/1.0 ");
    snprintf(num, sizeof(num), "%d ", status);
    hl = rt_append_str(head, hl, sizeof(head), num);
    hl = rt_append_str(head, hl, sizeof(head), rt_status_reason(status));
    if (!ctypeSet)
        hl = rt_append_str(head, hl, sizeof(head), "\r\nContent-Type: text/html; charset=utf-8");
    for (int i = 0; i < r->hdrCount; i++)
    {
        hl = rt_append_str(head, hl, sizeof(head), "\r\n");
        hl = rt_append_str(head, hl, sizeof(head), r->hdrs[i]);
    }
    for (int i = 0; i < r->setckCount; i++)
    {
        hl = rt_append_str(head, hl, sizeof(head), "\r\nSet-Cookie: ");
        hl = rt_append_str(head, hl, sizeof(head), r->setck[i]);
    }
    hl = rt_append_str(head, hl, sizeof(head), "\r\nContent-Length: ");
    snprintf(num, sizeof(num), "%lld", blen);
    hl = rt_append_str(head, hl, sizeof(head), num);
    hl = rt_append_str(head, hl, sizeof(head), "\r\nConnection: close\r\n\r\n");
    if (hl < 0)
    {
        rt_rq_release(r);
        return -1;
    }

    long long sent = rt_tcp_send_two(r->sock, head, hl, body, blen);
    rt_rq_release(r);
    return (sent >= 0 && sent >= hl + blen) ? 0 : -1;
}

void rt_http_req_close(long long req)
{
    rt_rq_release((rt_http_rq *)(intptr_t)req);
}

// ---- accept with timeout ----

// handle as usual, 0 when nobody connected in time, -1 on error. timeout is
// one-shot: the underlying accept stays blocking afterwards
long long rt_http_accept_timeout(long long listener, int raw, int ms)
{
    fd_set set;
    struct timeval tv;

    FD_ZERO(&set);
    FD_SET((SOCKET)listener, &set);
    tv.tv_sec = ms / 1000;
    tv.tv_usec = (ms % 1000) * 1000;

    int r = select((int)listener + 1, &set, NULL, NULL, &tv);
    if (r == 0) return 0;
    if (r < 0) return -1;
    return rt_http_accept(listener, raw);
}

// ---- forwarding ----

// relays both directions until both sides close, half-closing the far end as
// each side finishes. returns bytes moved a -> b
static long long rt_relay(long long a, long long b)
{
    long long total = 0;
    int aOpen = 1, bOpen = 1;
    char buf[8192];

    while (aOpen || bOpen)
    {
        fd_set rf;
        FD_ZERO(&rf);
        if (aOpen) FD_SET((SOCKET)a, &rf);
        if (bOpen) FD_SET((SOCKET)b, &rf);
        if (!aOpen && !bOpen) break;

        struct timeval tv;
        tv.tv_sec = 8;
        tv.tv_usec = 0;

        int r = select((int)(a > b ? a : b) + 1, &rf, NULL, NULL, &tv);
        if (r < 0) break;
        if (r == 0) continue;

        if (aOpen && FD_ISSET((SOCKET)a, &rf))
        {
            long n = recv((SOCKET)a, buf, sizeof(buf), 0);
            if (n <= 0)
            {
                aOpen = 0;
                shutdown((SOCKET)b, SD_SEND);
            }
            else
            {
                if (rt_tcp_send(b, buf, n) < 0) break;
                total += n;
            }
        }

        if (bOpen && FD_ISSET((SOCKET)b, &rf))
        {
            long n = recv((SOCKET)b, buf, sizeof(buf), 0);
            if (n <= 0)
            {
                bOpen = 0;
                shutdown((SOCKET)a, SD_SEND);
            }
            else
            {
                if (rt_tcp_send(a, buf, n) < 0) break;
            }
        }
    }

    return total;
}

// pushes the packet to an upstream server and relays both directions until
// the exchange ends. buffered bytes (raw or parsed) go first. the packet is
// consumed either way, like Respond
long long rt_http_forward(long long req, const char *host, int port)
{
    rt_http_rq *r = (rt_http_rq *)(intptr_t)req;
    if (!r || !host) return -1;

    long long up = rt_tcp_connect(host, port);
    if (up < 0) return -1;

    if (r->buf && r->len > 0)
    {
        if (rt_tcp_send(up, r->buf, r->len) < 0)
        {
            rt_tcp_close(up);
            return -1;
        }
    }

    long long moved = rt_relay(r->sock, up);
    rt_tcp_close(up);
    rt_rq_release(r);
    return moved;
}

// ---- clock ----

long long rt_clock_ms(void)
{
#ifdef _WIN32
    static LARGE_INTEGER freq;
    static int init = 0;
    if (!init)
    {
        QueryPerformanceFrequency(&freq);
        init = 1;
    }
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return c.QuadPart / (freq.QuadPart / 1000);
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (long long)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
#endif
}

long long rt_unixtime(void)
{
    return (long long)time(NULL);
}

// strftime with the usual %Y/%m/%d/%H/%M/%S codes; NULL when the format
// does not fit or the timestamp is out of range
char *rt_fmttime(long long unixTime, const char *fmt)
{
    if (!fmt) return NULL;
    time_t t = (time_t)unixTime;
    struct tm tmv;
#ifdef _WIN32
    if (localtime_s(&tmv, &t) != 0) return NULL;
#else
    if (!localtime_r(&t, &tmv)) return NULL;
#endif
    char buf[256];
    size_t n = strftime(buf, sizeof(buf), fmt, &tmv);
    if (n == 0) return NULL;
    return rt_rq_dup(buf, (long long)n);
}

// fixed decimals of a float, "%.*f"
char *rt_format_float(double v, int decimals)
{
    if (decimals < 0) decimals = 0;
    if (decimals > 15) decimals = 15;
    char buf[64];
    int n = snprintf(buf, sizeof(buf), "%.*f", decimals, v);
    if (n <= 0) return NULL;
    return rt_rq_dup(buf, n);
}

// ---- program arguments and environment ----

static int rt_argc_g;
static char **rt_argv_g;

void rt_set_args(int argc, char **argv)
{
    rt_argc_g = argc;
    rt_argv_g = argv;
}

long long rt_args_count(void)
{
    return rt_argc_g;
}

// borrowed pointer into the process argv, never NULL-checked at the call
// site: out-of-range yields NULL, which the language sees as a null string
char *rt_args_get(long long i)
{
    if (i < 0 || i >= rt_argc_g || !rt_argv_g) return NULL;
    return rt_argv_g[i];
}

char *rt_env(const char *name)
{
    if (!name) return NULL;
    return getenv(name);
}

// ---- string toolkit ----
//
// the list layout here mirrors the compiler's hs.list exactly: a 16-byte
// header of {data, size, cap} with elements boxed as i64. split bumps the
// live counter the same way the ir prelude does so drops stay balanced

typedef struct
{
    char *data;
    int size;
    int cap;
} rt_list_head;

static char *rt_list_push(rt_list_head *l, char *owned)
{
    if (l->size >= l->cap) return NULL;
    ((long long *)l->data)[l->size] = (long long)(intptr_t)owned;
    l->size++;
    return owned;
}

char *rt_split(const char *s, const char *sep)
{
    if (!s || !sep || !*sep) return NULL;

    long long sepLen = (long long)strlen(sep);
    long long parts = 1;
    const char *p = s;
    while ((p = strstr(p, sep)) != NULL)
    {
        parts++;
        p += sepLen;
    }
    if (parts > 100000) return NULL;

    rt_list_head *l = malloc(sizeof(rt_list_head));
    if (!l) return NULL;
    rt_live_inc();
    l->cap = (int)parts;
    l->size = 0;
    l->data = malloc((size_t)parts * 8);
    if (!l->data)
    {
        free(l);
        return NULL;
    }
    rt_live_inc();

    const char *cur = s;
    while (1)
    {
        const char *hit = strstr(cur, sep);
        long long len = hit ? hit - cur : (long long)strlen(cur);
        char *part = rt_rq_dup(cur, len);
        if (!part || !rt_list_push(l, part)) break;
        if (!hit) break;
        cur = hit + sepLen;
    }

    return (char *)l;
}

char *rt_join(long long list, const char *sep)
{
    rt_list_head *l = (rt_list_head *)(intptr_t)list;
    if (!l || !sep) return NULL;

    long long sepLen = (long long)strlen(sep);
    long long total = 1;
    for (int i = 0; i < l->size; i++)
    {
        char *part = (char *)(intptr_t)((long long *)l->data)[i];
        if (part) total += (long long)strlen(part);
        if (i + 1 < l->size) total += sepLen;
    }

    char *out = malloc((size_t)total);
    if (!out) return NULL;
    long long at = 0;
    for (int i = 0; i < l->size; i++)
    {
        char *part = (char *)(intptr_t)((long long *)l->data)[i];
        if (part)
        {
            long long len = (long long)strlen(part);
            if (at + len >= total) break;
            memcpy(out + at, part, (size_t)len);
            at += len;
        }
        if (i + 1 < l->size && sepLen > 0)
        {
            if (at + sepLen >= total) break;
            memcpy(out + at, sep, (size_t)sepLen);
            at += sepLen;
        }
    }
    out[at] = 0;
    rt_live_inc();
    return out;
}

char *rt_replace(const char *s, const char *from, const char *to)
{
    if (!s || !from || !to) return NULL;
    long long fromLen = (long long)strlen(from);
    if (fromLen == 0) return rt_rq_dup(s, (long long)strlen(s));

    long long toLen = (long long)strlen(to);
    long long hits = 0;
    const char *p = s;
    while ((p = strstr(p, from)) != NULL)
    {
        hits++;
        p += fromLen;
    }

    long long outLen = (long long)strlen(s) + hits * (toLen - fromLen);
    char *out = malloc((size_t)outLen + 1);
    if (!out) return NULL;

    const char *cur = s;
    long long at = 0;
    while (1)
    {
        const char *hit = strstr(cur, from);
        long long len = hit ? hit - cur : (long long)strlen(cur);
        if (at + len > outLen) break;
        memcpy(out + at, cur, (size_t)len);
        at += len;
        if (!hit) break;
        if (at + toLen > outLen) break;
        memcpy(out + at, to, (size_t)toLen);
        at += toLen;
        cur = hit + fromLen;
    }
    out[at] = 0;
    rt_live_inc();
    return out;
}

char *rt_trim(const char *s)
{
    if (!s) return NULL;
    long long len = (long long)strlen(s);
    long long a = 0, b = len;
    while (a < b && (s[a] == ' ' || s[a] == '\t' || s[a] == '\n' || s[a] == '\r')) a++;
    while (b > a && (s[b - 1] == ' ' || s[b - 1] == '\t' || s[b - 1] == '\n' || s[b - 1] == '\r')) b--;
    return rt_rq_dup(s + a, b - a);
}

char *rt_case_fold(const char *s, int upper)
{
    if (!s) return NULL;
    long long len = (long long)strlen(s);
    char *out = rt_rq_dup(s, len);
    if (!out) return NULL;
    for (long long i = 0; i < len; i++)
    {
        char c = out[i];
        if (upper && c >= 'a' && c <= 'z') out[i] = (char)(c - 32);
        if (!upper && c >= 'A' && c <= 'Z') out[i] = (char)(c + 32);
    }
    return out;
}

// ---- byte buffers ----
//
// same shape as the list header: {data, len}. owned like a string, freed
// through the language's drop machinery. binary safe, no NUL termination

typedef struct
{
    char *data;
    long long len;
} rt_buf;

void *rt_buf_new(long long n)
{
    if (n < 0 || n > 16 * 1024 * 1024) return NULL;
    rt_buf *b = malloc(sizeof(rt_buf));
    if (!b) return NULL;
    rt_live_inc();
    b->data = malloc((size_t)n + 1);
    if (!b->data)
    {
        free(b);
        return NULL;
    }
    rt_live_inc();
    memset(b->data, 0, (size_t)n + 1);
    b->len = n;
    return b;
}

long long rt_buf_len(long long buf)
{
    rt_buf *b = (rt_buf *)(intptr_t)buf;
    if (!b) return 0;
    return b->len;
}

long long rt_buf_get(long long buf, long long i, int *err)
{
    rt_buf *b = (rt_buf *)(intptr_t)buf;
    if (!b || i < 0 || i >= b->len)
    {
        rt_error_set("buffer index out of bounds");
        *err = 1;
        return 0;
    }
    return (unsigned char)b->data[i];
}

void rt_buf_set(long long buf, long long i, long long v, int *err)
{
    rt_buf *b = (rt_buf *)(intptr_t)buf;
    if (!b || i < 0 || i >= b->len)
    {
        rt_error_set("buffer index out of bounds");
        *err = 1;
        return;
    }
    if (v < 0 || v > 255)
    {
        rt_error_set("buffer bytes are 0-255");
        *err = 1;
        return;
    }
    b->data[i] = (char)v;
}

void rt_buf_drop(long long buf)
{
    rt_buf *b = (rt_buf *)(intptr_t)buf;
    if (!b) return;
    free(b->data);
    rt_live_dec();
    free(b);
    rt_live_dec();
}

void *rt_buf_from_str(const char *s)
{
    if (!s) return NULL;
    long long len = (long long)strlen(s);
    rt_buf *b = malloc(sizeof(rt_buf));
    if (!b) return NULL;
    rt_live_inc();
    b->data = malloc((size_t)len + 1);
    if (!b->data)
    {
        free(b);
        return NULL;
    }
    rt_live_inc();
    memcpy(b->data, s, (size_t)len + 1);
    b->len = len;
    return b;
}

// the copy is NUL-safe only when the bytes are; the language treats the
// result as a plain string
char *rt_buf_to_str(long long buf)
{
    rt_buf *b = (rt_buf *)(intptr_t)buf;
    if (!b) return rt_rq_dup("", 0);
    return rt_rq_dup(b->data, b->len);
}

// one recv into the buffer's capacity. returns bytes read: 0 is an orderly
// close, -1 a hard error
long long rt_recv_bytes(long long sock, long long buf)
{
    rt_buf *b = (rt_buf *)(intptr_t)buf;
    if (!b || b->len <= 0) return -1;
    return recv((SOCKET)sock, b->data, (int)b->len, 0);
}

// reads until the peer closes or cap is reached. NULL on error
void *rt_recv_all(long long sock, long long cap)
{
    if (cap <= 0 || cap > 16 * 1024 * 1024) return NULL;

    rt_buf *b = malloc(sizeof(rt_buf));
    if (!b) return NULL;
    rt_live_inc();
    b->data = malloc((size_t)cap + 1);
    if (!b->data)
    {
        free(b);
        return NULL;
    }
    rt_live_inc();
    b->len = 0;

    while (b->len < cap)
    {
        long n = recv((SOCKET)sock, b->data + b->len, (int)(cap - b->len), 0);
        if (n < 0)
        {
            rt_buf_drop((long long)(intptr_t)b);
            return NULL;
        }
        if (n == 0) break;
        b->len += n;
    }
    b->data[b->len] = 0;
    return b;
}

long long rt_send_bytes(long long sock, long long buf)
{
    rt_buf *b = (rt_buf *)(intptr_t)buf;
    if (!b) return -1;
    return rt_tcp_send(sock, b->data, b->len);
}

// ---- string builder ----
//
// amortized appending: capacity doubles, one final copy out to a string

typedef struct
{
    char *data;
    long long len, cap;
} rt_sb;

void *rt_sb_new(void)
{
    rt_sb *sb = malloc(sizeof(rt_sb));
    if (!sb) return NULL;
    rt_live_inc();
    sb->cap = 64;
    sb->len = 0;
    sb->data = malloc((size_t)sb->cap);
    if (!sb->data)
    {
        free(sb);
        return NULL;
    }
    rt_live_inc();
    sb->data[0] = 0;
    return sb;
}

static int rt_sb_room(rt_sb *sb, long long extra)
{
    if (sb->len + extra + 1 <= sb->cap) return 1;
    long long ncap = sb->cap;
    while (sb->len + extra + 1 > ncap) ncap *= 2;
    char *nd = realloc(sb->data, (size_t)ncap);
    if (!nd) return 0;
    sb->data = nd;
    sb->cap = ncap;
    return 1;
}

void rt_sb_add_str(void *vh, const char *s)
{
    rt_sb *sb = (rt_sb *)vh;
    if (!sb || !s) return;
    long long len = (long long)strlen(s);
    if (!rt_sb_room(sb, len)) return;
    memcpy(sb->data + sb->len, s, (size_t)len);
    sb->len += len;
    sb->data[sb->len] = 0;
}

void rt_sb_add_int(void *vh, long long v)
{
    char tmp[32];
    snprintf(tmp, sizeof(tmp), "%lld", v);
    rt_sb_add_str(vh, tmp);
}

void rt_sb_add_float(void *vh, double v)
{
    char tmp[40];
    snprintf(tmp, sizeof(tmp), "%g", v);
    rt_sb_add_str(vh, tmp);
}

void rt_sb_add_buf(void *vh, long long buf)
{
    rt_buf *b = (rt_buf *)(intptr_t)buf;
    rt_sb *sb = (rt_sb *)vh;
    if (!sb || !b) return;
    if (!rt_sb_room(sb, b->len)) return;
    memcpy(sb->data + sb->len, b->data, (size_t)b->len);
    sb->len += b->len;
    sb->data[sb->len] = 0;
}

char *rt_sb_str(void *vh)
{
    rt_sb *sb = (rt_sb *)vh;
    if (!sb) return rt_rq_dup("", 0);
    return rt_rq_dup(sb->data, sb->len);
}

void rt_sb_drop(void *vh)
{
    rt_sb *sb = (rt_sb *)vh;
    if (!sb) return;
    free(sb->data);
    rt_live_dec();
    free(sb);
    rt_live_dec();
}

// ---- hash maps ----
//
// map<string|int, string|int>. chained buckets, keys and string values are
// private copies. values travel as i64 like list elements

typedef struct rt_ment
{
    long long key, val;
    struct rt_ment *next;
} rt_ment;

typedef struct
{
    rt_ment **buckets;
    int cap, size;
    int keyStr, valStr;
} rt_map;

static long long rt_map_hash(long long key, int keyStr)
{
    unsigned long long h = 5381;
    if (keyStr)
    {
        const char *s = (const char *)(intptr_t)key;
        while (*s) h = h * 33 + (unsigned char)*s++;
    }
    else
    {
        h = (unsigned long long)key;
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdULL;
        h ^= h >> 29;
    }
    return (long long)h;
}

static rt_ment *rt_map_find(rt_map *m, long long key)
{
    long long h = rt_map_hash(key, m->keyStr) & (m->cap - 1);
    for (rt_ment *e = m->buckets[h]; e; e = e->next)
    {
        if (m->keyStr)
        {
            if (strcmp((const char *)(intptr_t)e->key, (const char *)(intptr_t)key) == 0) return e;
        }
        else if (e->key == key) return e;
    }
    return NULL;
}

void *rt_map_new(int keyStr, int valStr)
{
    rt_map *m = malloc(sizeof(rt_map));
    if (!m) return NULL;
    rt_live_inc();
    m->cap = 16;
    m->size = 0;
    m->keyStr = keyStr;
    m->valStr = valStr;
    m->buckets = calloc((size_t)m->cap, sizeof(rt_ment *));
    if (!m->buckets)
    {
        free(m);
        return NULL;
    }
    rt_live_inc();
    return m;
}

static void rt_map_grow(rt_map *m)
{
    int ncap = m->cap * 2;
    rt_ment **nb = calloc((size_t)ncap, sizeof(rt_ment *));
    if (!nb) return;

    for (int i = 0; i < m->cap; i++)
    {
        rt_ment *e = m->buckets[i];
        while (e)
        {
            rt_ment *next = e->next;
            long long h = rt_map_hash(e->key, m->keyStr) & (ncap - 1);
            e->next = nb[h];
            nb[h] = e;
            e = next;
        }
    }
    free(m->buckets);
    m->buckets = nb;
    m->cap = ncap;
}

// key and val are boxed i64s; the map takes ownership of the string copies
// the caller made. on an existing key the incoming key copy is freed here
void rt_map_insert(long long vh, long long key, long long val)
{
    rt_map *m = (rt_map *)vh;
    if (!m) return;

    rt_ment *e = rt_map_find(m, key);
    if (e)
    {
        if (m->keyStr)
        {
            free((void *)(intptr_t)key);
            rt_live_dec();
        }
        if (m->valStr && e->val != val)
        {
            free((void *)(intptr_t)e->val);
            rt_live_dec();
        }
        e->val = val;
        return;
    }

    if (m->size >= m->cap * 2) rt_map_grow(m);

    e = malloc(sizeof(rt_ment));
    if (!e) return;
    long long h = rt_map_hash(key, m->keyStr) & (m->cap - 1);
    e->key = key;
    e->val = val;
    e->next = m->buckets[h];
    m->buckets[h] = e;
    m->size++;
}

// returns the value, 0 when missing; *found says which
long long rt_map_get(long long vh, long long key, int *found)
{
    rt_map *m = (rt_map *)vh;
    if (!m)
    {
        *found = 0;
        return 0;
    }
    rt_ment *e = rt_map_find(m, key);
    *found = e != NULL;
    return e ? e->val : 0;
}

int rt_map_contains(long long vh, long long key)
{
    rt_map *m = (rt_map *)vh;
    return m && rt_map_find(m, key) != NULL;
}

void rt_map_remove(long long vh, long long key)
{
    rt_map *m = (rt_map *)vh;
    if (!m) return;
    long long h = rt_map_hash(key, m->keyStr) & (m->cap - 1);
    rt_ment **p = &m->buckets[h];
    while (*p)
    {
        rt_ment *e = *p;
        int hit = m->keyStr
            ? strcmp((const char *)(intptr_t)e->key, (const char *)(intptr_t)key) == 0
            : e->key == key;
        if (hit)
        {
            *p = e->next;
            if (m->keyStr)
            {
                free((void *)(intptr_t)e->key);
                rt_live_dec();
            }
            if (m->valStr)
            {
                free((void *)(intptr_t)e->val);
                rt_live_dec();
            }
            free(e);
            m->size--;
            return;
        }
        p = &e->next;
    }
}

long long rt_map_count(long long vh)
{
    rt_map *m = (rt_map *)vh;
    return m ? m->size : 0;
}

// keys or values of a map as a fresh list; string elements are duplicated
// so the list owns its own copies and can drop normally
char *rt_map_items(long long vh, int wantValues, int kindStr)
{
    rt_map *m = (rt_map *)vh;
    if (!m || m->size == 0) return NULL;

    rt_list_head *l = malloc(sizeof(rt_list_head));
    if (!l) return NULL;
    rt_live_inc();
    l->cap = m->size;
    l->size = 0;
    l->data = malloc((size_t)(m->size > 0 ? m->size : 1) * 8);
    if (!l->data)
    {
        free(l);
        return NULL;
    }
    rt_live_inc();

    for (int i = 0; i < m->cap; i++)
    {
        rt_ment *e = m->buckets[i];
        while (e)
        {
            long long v = wantValues ? e->val : e->key;
            if (kindStr && v)
                v = (long long)rt_rq_dup((const char *)(intptr_t)v, (long long)strlen((const char *)(intptr_t)v));
            ((long long *)l->data)[l->size] = v;
            l->size++;
            e = e->next;
        }
    }
    return (char *)l;
}

void rt_map_clear(long long vh)
{
    rt_map *m = (rt_map *)vh;
    if (!m) return;
    for (int i = 0; i < m->cap; i++)
    {
        rt_ment *e = m->buckets[i];
        while (e)
        {
            rt_ment *next = e->next;
            if (m->keyStr)
            {
                free((void *)(intptr_t)e->key);
                rt_live_dec();
            }
            if (m->valStr)
            {
                free((void *)(intptr_t)e->val);
                rt_live_dec();
            }
            free(e);
            e = next;
        }
        m->buckets[i] = NULL;
    }
    m->size = 0;
}

void rt_map_drop(long long vh)
{
    rt_map *m = (rt_map *)vh;
    if (!m) return;
    rt_map_clear(vh);
    free(m->buckets);
    rt_live_dec();
    free(m);
    rt_live_dec();
}

static int _stricmp_raw(const char *a, const char *b)
{
#ifdef _WIN32
    return _stricmp(a, b);
#else
    while (*a && *b)
    {
        int ca = *a++, cb = *b++;
        if (ca >= 'A' && ca <= 'Z') ca += 32;
        if (cb >= 'A' && cb <= 'Z') cb += 32;
        if (ca != cb) return ca - cb;
    }
    return *a - *b;
#endif
}
