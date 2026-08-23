// H# runtime, linked into every compiled program.
// threads, tasks, sockets and a bit of HTTP, all plain C with no allocations
// of its own beyond what the APIs need.

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
typedef HANDLE rt_thread;
#else
#include <pthread.h>
#include <unistd.h>
#include <sys/socket.h>
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
#endif

#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

// forward declarations so ordering doesn't matter below
void rt_pool_start(void);
static int _stricmp_raw(const char *a, const char *b);

// ---- lifecycle ----

static int rt_ready = 0;

void rt_init(void)
{
    if (rt_ready) return;
#ifdef _WIN32
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
#endif
    rt_pool_start();
    rt_ready = 1;
}

// ---- atomic live-allocation counter (backs mem()) ----

static _Atomic long rt_live = 0;

void rt_live_inc(void) { atomic_fetch_add(&rt_live, 1); }
void rt_live_dec(void) { atomic_fetch_sub(&rt_live, 1); }
long rt_live_get(void) { return atomic_load(&rt_live); }

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
#ifdef _WIN32
    InitializeCriticalSection(&t->lock);
    InitializeConditionVariable(&t->finished);
#else
    pthread_mutex_init(&t->lock, NULL);
    pthread_cond_init(&t->finished, NULL);
#endif
    return t;
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
}

void rt_task_submit(rt_task *t)
{
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

// ---- sockets ----

static long rt_sock_err(void)
{
#ifdef _WIN32
    return -WSAGetLastError();
#else
    return -1;
#endif
}

long rt_tcp_listen(int port)
{
    long s = socket(AF_INET, SOCK_STREAM, 0);
    if (s == INVALID_SOCKET) return rt_sock_err();

    int one = 1;
    setsockopt((SOCKET)s, SOL_SOCKET, SO_REUSEADDR, (const char *)&one, sizeof(one));

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = INADDR_ANY;
    addr.sin_port = htons((unsigned short)port);

    if (bind((SOCKET)s, (struct sockaddr *)&addr, sizeof(addr)) != 0)
    {
        long e = rt_sock_err();
        closesocket((SOCKET)s);
        return e;
    }
    if (listen((SOCKET)s, 16) != 0)
    {
        long e = rt_sock_err();
        closesocket((SOCKET)s);
        return e;
    }
    return s;
}

long rt_tcp_accept(long listener)
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

long rt_tcp_connect(const char *host, int port)
{
    char portstr[16];
    snprintf(portstr, sizeof(portstr), "%d", port);

    struct addrinfo hints, *res = NULL;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;

    if (getaddrinfo(host, portstr, &hints, &res) != 0 || !res) return rt_sock_err();

    SOCKET s = socket(res->ai_family, res->ai_socktype, res->ai_protocol);
    if (s == INVALID_SOCKET)
    {
        freeaddrinfo(res);
        return rt_sock_err();
    }
    if (connect(s, res->ai_addr, (int)res->ai_addrlen) != 0)
    {
        long e = rt_sock_err();
        closesocket(s);
        freeaddrinfo(res);
        return e;
    }
    freeaddrinfo(res);
    return s;
}

long rt_tcp_send(long sock, const char *s, long len)
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
long rt_tcp_recv(long sock, char *buf, long cap)
{
    long n = recv((SOCKET)sock, buf, (int)cap, 0);
    if (n == 0) return 0;
    if (n < 0) return rt_sock_err();
    return n;
}

void rt_tcp_close(long sock)
{
    closesocket((SOCKET)sock);
}

long rt_udp_open(void)
{
    SOCKET s = socket(AF_INET, SOCK_DGRAM, 0);
    if (s == INVALID_SOCKET) return rt_sock_err();
    return s;
}

long rt_udp_sendto(long sock, const char *host, int port, const char *s, long len)
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
long rt_udp_recvfrom(long sock, char *buf, long cap, char *sender_host)
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
// "" on orderly close, NULL on error. the caller owns the buffer
char *rt_tcp_line(long sock)
{
    char *buf = malloc(8192);
    long used = 0;

    while (used < 8191)
    {
        char c;
        long n = recv((SOCKET)sock, &c, 1, 0);
        if (n < 0)
        {
            free(buf);
            return NULL;
        }
        if (n == 0) break;
        if (c == '\n') break;
        if (c != '\r') buf[used++] = c;
    }

    buf[used] = 0;
    rt_live_inc();
    return buf;
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
static int rt_url_split(const char *url, char *host, int *port, char *path)
{
    if (strncmp(url, "http://", 7) == 0)
    {
        url += 7;
    }
    else if (strncmp(url, "https://", 8) == 0)
    {
        return -2;
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
    *port = 80;
    if (colon)
    {
        *colon = 0;
        *port = atoi(colon + 1);
        if (*port <= 0) return -1;
    }

    snprintf(path, 1024, "%s", slash ? slash : "/");
    return 0;
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
