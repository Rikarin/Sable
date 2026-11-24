// Port of channel.sb: two threads and std's mpsc channel.

use std::sync::mpsc;
use std::thread;

fn main() {
    let (sender, receiver) = mpsc::channel::<i64>();
    let producer = thread::spawn(move || {
        for i in 0..500000 {
            sender.send(i).unwrap();
        }
    });
    let mut total: i64 = 0;
    for value in receiver {
        total += value;
    }
    producer.join().unwrap();
    println!("{total}");
}
