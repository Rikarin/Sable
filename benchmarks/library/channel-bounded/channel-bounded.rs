// Port of channel-bounded.sb: five threads and std's sync_channel.

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::sync::mpsc;
use std::thread;

fn main() {
    bench::run(|| {
        let (sender, receiver) = mpsc::sync_channel::<i64>(64);
        let mut producers = Vec::new();
        for p in 0..4 {
            let sender = sender.clone();
            producers.push(thread::spawn(move || {
                for i in 0..25000 {
                    sender.send(i + p).unwrap();
                }
            }));
        }
        drop(sender);
        let mut total: i64 = 0;
        for value in receiver {
            total += value;
        }
        for producer in producers {
            producer.join().unwrap();
        }
        format!("{total}")
    });
}
