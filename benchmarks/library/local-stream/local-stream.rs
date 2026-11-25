// Port of local-stream.sb: a Unix domain socket, the server on a thread of
// its own. (Unix only, as std's local sockets are.)

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::io::{Read, Write};
use std::os::unix::net::{UnixListener, UnixStream};
use std::thread;

fn main() {
    bench::run(|| {
        let path = format!("/tmp/sable-bench-rs-{}.socket", std::process::id());
        let _ = std::fs::remove_file(&path);
        let listener = UnixListener::bind(&path).unwrap();
        let serving = thread::spawn(move || {
            let (mut connection, _) = listener.accept().unwrap();
            let mut buffer = vec![0u8; 4096];
            loop {
                let got = connection.read(&mut buffer).unwrap();
                if got == 0 {
                    break;
                }
                connection.write_all(&buffer[..got]).unwrap();
            }
        });
        let mut client = UnixStream::connect(&path).unwrap();
        let message = [7u8; 64];
        let mut into = [0u8; 64];
        let mut total: i64 = 0;
        for _ in 0..10_000 {
            client.write_all(&message).unwrap();
            let mut got = 0;
            while got < 64 {
                let read = client.read(&mut into[..64 - got]).unwrap();
                got += read;
                total += into[0] as i64;
            }
        }
        drop(client);
        serving.join().unwrap();
        let _ = std::fs::remove_file(&path);
        format!("{total}")
    });
}
