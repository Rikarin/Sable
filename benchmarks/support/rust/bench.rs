// What the Rust ports share: the measuring, as Sable's Bencher does it
// (../../README.md, "How it is measured"). A port includes it as a module,
//
//     #[path = "../../support/rust/bench.rs"]
//     mod bench;
//
// and its main is 'bench::run(|| ...)', of a closure that does the work and
// gives what it computed. Run without arguments, the program does
// that once and prints it; with '--measure', it prints 'check' and the
// samples, as support/dotnet/Program.cs does.

use std::hint::black_box;
use std::time::Instant;

#[repr(C)]
struct TimeSpec {
    seconds: i64,
    nanoseconds: i64,
}

#[repr(C)]
struct TimeVal {
    seconds: i64,
    microseconds: i32,
    padding: i32,
}

// 64-bit Linux's and macOS's: a timeval's microseconds fit an i32 in both.
#[repr(C)]
struct Usage {
    user: TimeVal,
    system: TimeVal,
    rest: [i64; 14],
}

extern "C" {
    fn clock_gettime(clock: i32, time: *mut TimeSpec) -> i32;
    fn getrusage(who: i32, usage: *mut Usage) -> i32;
}

// This process's processor time, and that of the processes it waited for.
fn processor() -> f64 {
    let clock = if cfg!(target_os = "macos") { 12 } else { 2 };
    unsafe {
        let mut time = TimeSpec { seconds: 0, nanoseconds: 0 };
        clock_gettime(clock, &mut time);
        let mut usage: Usage = std::mem::zeroed();
        getrusage(-1, &mut usage);
        time.seconds as f64 * 1e9
            + time.nanoseconds as f64
            + (usage.user.seconds + usage.system.seconds) as f64 * 1e9
            + (usage.user.microseconds as f64 + usage.system.microseconds as f64) * 1e3
    }
}

pub fn run<F: FnMut() -> String>(mut work: F) {
    let args: Vec<String> = std::env::args().collect();
    if args.len() == 1 {
        println!("{}", work());
        return;
    }
    let option = |name: &str, otherwise: f64| {
        args.iter().position(|a| a == name).map_or(otherwise, |at| args[at + 1].parse().unwrap())
    };
    let warm_up = option("--warm-up-ms", 100.0) * 1e6;
    let sample = option("--sample-ms", 10.0) * 1e6;
    let samples = option("--samples", 20.0) as usize;
    let budget = option("--budget-ms", 3000.0) * 1e6;

    let mut check = String::new();
    let mut iterate = |iterations: u64| {
        let used = processor();
        let begun = Instant::now();
        for _ in 0..iterations {
            check = black_box(work());
        }
        (begun.elapsed().as_nanos() as f64, processor() - used)
    };

    // The pilot.
    let mut iterations = 1u64;
    let mut warmed = 0.0;
    let mut wall;
    loop {
        wall = iterate(iterations).0;
        warmed += wall;
        if wall * 8.0 >= sample {
            break;
        }
        iterations *= 2;
    }
    let choose = |per: f64| ((sample / per).ceil() as u64).max(1);
    iterations = choose(wall / iterations as f64);

    // The warm-up.
    let (mut last, mut runs, mut rises) = (0.0, 0, 0);
    while warmed < warm_up || (rises < 2 && runs < 50 && warmed < 3.0 * warm_up) {
        wall = iterate(iterations).0;
        warmed += wall;
        runs += 1;
        let per = wall / iterations as f64;
        if runs > 1 && per >= last {
            rises += 1;
        }
        last = per;
        iterations = choose(per);
    }

    let mut lines = Vec::new();
    let mut measured = 0.0;
    while lines.len() < samples && (lines.len() < 5 || measured < budget) {
        let (wall, used) = iterate(iterations);
        measured += wall;
        lines.push(format!("sample\t{iterations}\t{wall}\t{used}"));
    }
    println!("check\t{}", check.replace('\\', "\\\\").replace('\n', "\\n").replace('\t', "\\t"));
    println!("{}", lines.join("\n"));
}
