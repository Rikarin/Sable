// Port of process-spawn.sb, with Command::output.

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::process::{Command, Stdio};

fn main() {
    bench::run(|| {
        let mut total: i64 = 0;
        for i in 0..100 {
            let done = Command::new("/bin/echo").arg(i.to_string()).stdin(Stdio::null()).output().unwrap();
            if done.status.success() {
                total += String::from_utf8_lossy(&done.stdout).trim().parse::<i64>().unwrap();
            }
        }
        format!("{total}")
    });
}
