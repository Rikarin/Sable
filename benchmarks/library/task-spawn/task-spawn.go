// Port of task-spawn.sb: a hundred batches of two thousand goroutines, each
// giving its number through a channel of its own (Go's nearest to a task
// whose value is awaited), received in order.

package main

import "fmt"

func main() {
	benchRun(func() string {
		var total int64
		for batch := int64(0); batch < 100; batch++ {
			var tasks []chan int64
			for i := int64(0); i < 2000; i++ {
				task := make(chan int64, 1)
				go func(n int64) { task <- n }(i + batch)
				tasks = append(tasks, task)
			}
			for _, task := range tasks {
				total += <-task
			}
		}
		return fmt.Sprint(total)
	})
}
