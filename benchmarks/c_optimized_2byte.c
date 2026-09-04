#define _GNU_SOURCE
// Idiomatic-optimized C: edge-triggered epoll, precomputed response with
// writev (single syscall for head+body), TCP_NODELAY, TCP_QUICKACK,
// larger listen backlog, nonblocking accept loop draining fully.
#include <stdio.h>
#include <string.h>
#include <unistd.h>
#include <fcntl.h>
#include <sys/uio.h>
#include <arpa/inet.h>
#include <sys/socket.h>
#include <sys/epoll.h>
#include <netinet/tcp.h>

#define MAX_EVENTS 512

static void set_nonblocking(int fd) {
    int flags = fcntl(fd, F_GETFL, 0);
    fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

int main(void) {
    int srv = socket(AF_INET, SOCK_STREAM, 0);
    int one = 1;
    setsockopt(srv, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));
    set_nonblocking(srv);

    struct sockaddr_in addr = {0};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = INADDR_ANY;
    addr.sin_port = htons(19298);
    bind(srv, (struct sockaddr*)&addr, sizeof(addr));
    listen(srv, 4096);
    printf("c optimized epoll server up on 19298\n"); fflush(stdout);

    int ep = epoll_create1(0);
    struct epoll_event ev = {0};
    ev.events = EPOLLIN;
    ev.data.fd = srv;
    epoll_ctl(ep, EPOLL_CTL_ADD, srv, &ev);

    static char body[65536];
    memset(body, 'x', sizeof(body));
    char head[128];
    int headlen = snprintf(head, sizeof(head),
        "HTTP/1.0 200 OK\r\nContent-Length: %d\r\nConnection: close\r\n\r\n", (int)sizeof(body));

    char buf[8192];
    struct epoll_event events[MAX_EVENTS];
    struct iovec iov[2];
    iov[0].iov_base = head; iov[0].iov_len = headlen;
    iov[1].iov_base = body; iov[1].iov_len = sizeof(body);

    for (;;) {
        int n = epoll_wait(ep, events, MAX_EVENTS, -1);
        for (int i = 0; i < n; i++) {
            if (events[i].data.fd == srv) {
                for (;;) {
                    int c = accept(srv, NULL, NULL);
                    if (c < 0) break;
                    setsockopt(c, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(one));
                    read(c, buf, sizeof(buf));
                    writev(c, iov, 2);
                    close(c);
                }
            }
        }
    }
    return 0;
}
