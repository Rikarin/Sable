// Port of channel-bounded.sb: four goroutines send into a channel with room
// for sixty-four, another receives, and the channel is closed when the
// senders are done.

package main

import (
	"fmt"
	"sync"
)

func main() {
	benchRun(func() string {
		channel := make(chan int64, 64)
		var producers sync.WaitGroup
		for p := int64(0); p < 4; p++ {
			producers.Add(1)
			go func(p int64) {
				defer producers.Done()
				for i := int64(0); i < 25000; i++ {
					channel <- i + p
				}
			}(p)
		}
		consumer := make(chan int64, 1)
		go func() {
			var total int64
			for value := range channel {
				total += value
			}
			consumer <- total
		}()
		producers.Wait()
		close(channel)
		return fmt.Sprint(<-consumer)
	})
}
