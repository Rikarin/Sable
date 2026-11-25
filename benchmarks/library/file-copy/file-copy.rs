// Port of file-copy.sb.

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::fs;
use std::io;

fn main() -> io::Result<()> {
    let directory = std::env::current_dir()?;
    let small = &directory.join("file-copy-rust-small.bin");
    let large = &directory.join("file-copy-rust-large.bin");
    let copy = &directory.join("file-copy-rust-copy.bin");
    fs::write(small, vec![7u8; 4000])?;
    fs::write(large, vec![9u8; 4000000])?;

    bench::run(|| {
        let work = || -> io::Result<String> {
            let mut total: u64 = 0;
            for _ in 0..1000 {
                fs::copy(small, copy)?;
                total += fs::metadata(copy)?.len();
            }
            for _ in 0..10 {
                fs::copy(large, copy)?;
                total += fs::metadata(copy)?.len();
            }
            let last = fs::read(copy)?;
            Ok(format!("{total} {} {}", last.len(), last[last.len() - 1]))
        };
        work().unwrap()
    });
    fs::remove_file(small)?;
    fs::remove_file(large)?;
    fs::remove_file(copy)?;
    Ok(())
}
