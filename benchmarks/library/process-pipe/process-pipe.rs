// Port of process-pipe.sb, with a thread to write.

use std::io::{Read, Write};
use std::process::{Command, Stdio};
use std::thread;

fn main() {
    let mut child = Command::new("cat").stdin(Stdio::piped()).stdout(Stdio::piped()).spawn().unwrap();
    let mut input = child.stdin.take().unwrap();
    let writing = thread::spawn(move || {
        let bytes = vec![42u8; 64000];
        for _ in 0..1000 {
            input.write_all(&bytes).unwrap();
        }
    });
    let mut output = child.stdout.take().unwrap();
    let mut buffer = vec![0u8; 65536];
    let mut count: i64 = 0;
    let mut total: i64 = 0;
    loop {
        let got = output.read(&mut buffer).unwrap();
        if got == 0 {
            break;
        }
        count += got as i64;
        total += (buffer[got / 2] - 42) as i64;
    }
    writing.join().unwrap();
    let status = child.wait().unwrap();
    println!("{count} {total} {}", status.success());
}
