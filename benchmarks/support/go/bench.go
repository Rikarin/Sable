// What the Go ports share: the measuring, as Sable's Bencher does it
// (../../README.md, "How it is measured"). A port is a 'package main'
// built with this file beside it,
//
//     go build -o port benchmarks/library/<name>/<name>.go benchmarks/support/go/bench.go
//
// and its main is 'benchRun(func() string { ... })', of a function that does
// the work and gives what it computed. Run without arguments, the program
// does that once and prints it; with '--measure', it prints 'check' and the
// samples, as support/rust/bench.rs does; with '--allocations', what the
// work allocates as the runtime counts it (every goroutine's, by
// runtime.ReadMemStats), and is not timed.
package main

import (
	"fmt"
	"math"
	"os"
	"runtime"
	"strconv"
	"strings"
	"syscall"
	"time"
)

// Kept where the optimiser cannot see that nothing reads it.
var benchSink string

// This process's processor time, of every thread, and that of the processes it waited for.
func benchProcessor() float64 {
	total := 0.0
	for _, who := range []int{syscall.RUSAGE_SELF, syscall.RUSAGE_CHILDREN} {
		var usage syscall.Rusage
		if err := syscall.Getrusage(who, &usage); err != nil {
			panic(err)
		}
		total += float64(usage.Utime.Nano() + usage.Stime.Nano())
	}
	return total
}

// Once for what the first run makes and keeps, then as many as are counted.
func benchCount(work func() string) {
	const runs = 3
	benchSink = work()
	var before, after runtime.MemStats
	runtime.ReadMemStats(&before)
	for i := 0; i < runs; i++ {
		benchSink = work()
	}
	runtime.ReadMemStats(&after)
	fmt.Printf("allocated\t%v\t%v\n", float64(after.TotalAlloc-before.TotalAlloc)/runs, float64(after.Mallocs-before.Mallocs)/runs)
}

func benchRun(work func() string) {
	args := os.Args
	if len(args) == 1 {
		fmt.Println(work())
		return
	}
	if args[1] == "--allocations" {
		benchCount(work)
		return
	}

	option := func(name string, otherwise float64) float64 {
		for i, a := range args {
			if a == name && i+1 < len(args) {
				value, err := strconv.ParseFloat(args[i+1], 64)
				if err != nil {
					panic(err)
				}
				return value
			}
		}
		return otherwise
	}
	warmUp := option("--warm-up-ms", 100) * 1e6
	sample := option("--sample-ms", 10) * 1e6
	samples := int(option("--samples", 20))
	budget := option("--budget-ms", 3000) * 1e6

	check := ""
	iterate := func(iterations uint64) (float64, float64) {
		used := benchProcessor()
		begun := time.Now()
		for i := uint64(0); i < iterations; i++ {
			check = work()
		}
		return float64(time.Since(begun).Nanoseconds()), benchProcessor() - used
	}

	// The pilot.
	iterations := uint64(1)
	warmed := 0.0
	var wall float64
	for {
		wall, _ = iterate(iterations)
		warmed += wall
		if wall*8 >= sample {
			break
		}
		iterations *= 2
	}
	choose := func(per float64) uint64 {
		return uint64(math.Max(math.Ceil(sample/per), 1))
	}
	iterations = choose(wall / float64(iterations))

	// The warm-up.
	last, runs, rises := 0.0, 0, 0
	for warmed < warmUp || (rises < 2 && runs < 50 && warmed < 3*warmUp) {
		wall, _ = iterate(iterations)
		warmed += wall
		runs++
		per := wall / float64(iterations)
		if runs > 1 && per >= last {
			rises++
		}
		last = per
		iterations = choose(per)
	}

	var lines []string
	measured := 0.0
	for len(lines) < samples && (len(lines) < 5 || measured < budget) {
		wall, used := iterate(iterations)
		measured += wall
		lines = append(lines, fmt.Sprintf("sample\t%d\t%v\t%v", iterations, wall, used))
	}
	escaped := strings.NewReplacer("\\", "\\\\", "\n", "\\n", "\t", "\\t").Replace(check)
	fmt.Printf("check\t%s\n%s\n", escaped, strings.Join(lines, "\n"))
}
