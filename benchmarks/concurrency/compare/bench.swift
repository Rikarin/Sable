// The concurrency benchmarks' counterparts in Swift (docs/04, "Async, as
// implemented"): spawn/await throughput, ping-pong through an actor, and a
// million tasks each sleeping a second. Build and run with
//     swiftc -O benchmarks/concurrency/compare/bench.swift -o bench && ./bench
import Foundation

func now() -> UInt64 { DispatchTime.now().uptimeNanoseconds }

func spawnAwait() async -> Int {
    let start = now()
    var total = 0
    for batch in 0..<100 {
        var tasks: [Task<Int, Never>] = []
        tasks.reserveCapacity(10_000)
        for i in 0..<10_000 {
            tasks.append(Task { i + batch })
        }
        for task in tasks {
            total += await task.value
        }
    }
    print("spawn+await: \((now() - start) / 1_000_000) ns per task")
    return total
}

actor Table {
    var ball = 0
    func turn() -> Int { ball }
    func take() { ball += 1 }
}

// Two tasks take turns through an actor, each yielding until it is its
// turn: Sable's ping-pong.sb, its yielding half.
func player(_ table: Table, _ me: Int, _ rounds: Int) async {
    while true {
        let ball = await table.turn()
        if ball >= rounds { return }
        if ball % 2 == me {
            await table.take()
        } else {
            await Task.yield()
        }
    }
}

func pingPong() async {
    let table = Table()
    let rounds = 100_000
    let start = now()
    async let a: Void = player(table, 0, rounds)
    async let b: Void = player(table, 1, rounds)
    _ = await (a, b)
    print("ping-pong through an actor, yielding: \((now() - start) / UInt64(rounds)) ns per round")
}

func sleepers(_ count: Int) async {
    let start = now()
    await withTaskGroup(of: Void.self) { group in
        for _ in 0..<count {
            group.addTask { try? await Task.sleep(nanoseconds: 1_000_000_000) }
        }
    }
    var usage = rusage()
    getrusage(RUSAGE_SELF, &usage)
    print("\(count) sleepers: \((now() - start) / 1_000_000) ms, peak \(usage.ru_maxrss / 1024 / 1024) MiB")
}

let which = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "all"
if which == "all" || which == "spawn" { _ = await spawnAwait() }
if which == "all" || which == "ping" { await pingPong() }
if which == "all" || which == "sleep" { await sleepers(1_000_000) }
