// The concurrency benchmarks' counterparts in Go (docs/04, "Async, as
// implemented"), as Bench.cs has them: spawn/await throughput, ping-pong
// between two goroutines, a million goroutines each sleeping a second, and
// fan-out-http.sb's fifty clients at once over loopback. A goroutine is
// Go's task, and a channel of room for one, written once, its awaited value.
// Build and run with
//     go build -o bench benchmarks/concurrency/compare/bench.go && ./bench
package main

import (
	"fmt"
	"io"
	"net"
	"os"
	"sync"
	"syscall"
	"time"
)

func spawnAwait() int64 {
	start := time.Now()
	var total int64
	for batch := 0; batch < 100; batch++ {
		tasks := make([]chan int, 10_000)
		for i := range tasks {
			task := make(chan int, 1)
			go func(n int) { task <- n }(i + batch)
			tasks[i] = task
		}
		for _, task := range tasks {
			total += int64(<-task)
		}
	}
	fmt.Printf("spawn+await: %d ns per task\n", time.Since(start).Nanoseconds()/1_000_000)
	return total
}

func pingPong() {
	ping, pong := make(chan int), make(chan int)
	const rounds = 1_000_000
	start := time.Now()
	done := make(chan struct{})
	go func() {
		for i := 0; i < rounds; i++ {
			pong <- <-ping + 1
		}
		close(done)
	}()
	value := 0
	for i := 0; i < rounds; i++ {
		ping <- value
		value = <-pong
	}
	<-done
	fmt.Printf("ping-pong through channels: %d ns per round\n", time.Since(start).Nanoseconds()/rounds)
}

func sleepers(count int) {
	start := time.Now()
	var group sync.WaitGroup
	group.Add(count)
	for i := 0; i < count; i++ {
		go func() {
			defer group.Done()
			time.Sleep(time.Second)
		}()
	}
	group.Wait()
	var usage syscall.Rusage
	_ = syscall.Getrusage(syscall.RUSAGE_SELF, &usage)
	fmt.Printf("%d sleepers: %d ms, peak rss %d (KiB on Linux, bytes on macOS)\n", count, time.Since(start).Milliseconds(), usage.Maxrss)
}

// fan-out-http.sb's: fifty clients at once, forty times over, each
// connecting, asking, and reading a 512-byte reply a goroutine of the
// server's writes.
func fanOut() {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		panic(err)
	}
	address := listener.Addr().String()
	reply := make([]byte, 512)
	for i := range reply {
		reply[i] = byte(48 + i%10)
	}

	served := make(chan struct{})
	go func() {
		defer close(served)
		for {
			connection, err := listener.Accept()
			if err != nil {
				return // closed: the run is over
			}
			go func(connection net.Conn) {
				defer connection.Close()
				request := make([]byte, 1024)
				_, _ = connection.Read(request)
				_, _ = connection.Write(reply)
			}(connection)
		}
	}()

	const clients, rounds = 50, 40
	start := time.Now()
	var bytes int64
	for round := 0; round < rounds; round++ {
		asking := make([]chan int, clients)
		for i := range asking {
			task := make(chan int, 1)
			asking[i] = task
			go func() {
				connection, err := net.Dial("tcp", address)
				if err != nil {
					panic(err)
				}
				defer connection.Close()
				connection.(*net.TCPConn).SetNoDelay(true)
				if _, err := connection.Write([]byte("GET /\n")); err != nil {
					panic(err)
				}
				buffer := make([]byte, 4096)
				total := 0
				for {
					got, err := connection.Read(buffer)
					total += got
					if err == io.EOF {
						break
					}
					if err != nil {
						panic(err)
					}
				}
				task <- total
			}()
		}
		for _, task := range asking {
			bytes += int64(<-task)
		}
	}

	fmt.Printf("fan-out over loopback: %d us per request, %d bytes each\n", time.Since(start).Microseconds()/(clients*rounds), bytes/(clients*rounds))
	listener.Close()
	<-served
}

func main() {
	which := "all"
	if len(os.Args) > 1 {
		which = os.Args[1]
	}
	if which == "all" || which == "spawn" {
		_ = spawnAwait()
	}
	if which == "all" || which == "ping" {
		pingPong()
	}
	if which == "all" || which == "sleep" {
		sleepers(1_000_000)
	}
	if which == "all" || which == "fanout" {
		fanOut()
	}
}
