// Port of os-pipe.sb: std::io::pipe, with a thread to write.

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::io::{Read, Write};
use std::thread;

fn main() {
    bench::run(|| {
        let (mut reader, mut writer) = std::io::pipe().unwrap();
        let writing = thread::spawn(move || {
            let bytes = vec![42u8; 64000];
            for _ in 0..1000 {
                writer.write_all(&bytes).unwrap();
            }
        });
        let mut buffer = vec![0u8; 65536];
        let mut count: i64 = 0;
        let mut total: i64 = 0;
        loop {
            let got = reader.read(&mut buffer).unwrap();
            if got == 0 {
                break;
            }
            count += got as i64;
            total += (buffer[got / 2] - 42) as i64;
        }
        writing.join().unwrap();
        format!("{count} {total}")
    });
}
