package main

import (
	"fmt"
	"net"
	"runtime"
)

func main() {
	runtime.GOMAXPROCS(runtime.NumCPU())
	resp := []byte("HTTP/1.0 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok")

	ln, err := net.Listen("tcp", ":19399")
	if err != nil {
		panic(err)
	}
	fmt.Println("go optimized small server up on 19399")

	for {
		conn, err := ln.Accept()
		if err != nil {
			continue
		}
		go func(c net.Conn) {
			defer c.Close()
			if tc, ok := c.(*net.TCPConn); ok {
				tc.SetNoDelay(true)
			}
			buf := make([]byte, 8192)
			c.Read(buf)
			c.Write(resp)
		}(conn)
	}
}
