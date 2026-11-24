// Port of file-lines.sb.

use std::fs::{self, File};
use std::io::{self, BufRead, BufReader};

fn main() -> io::Result<()> {
    let mut lines: Vec<String> = Vec::new();
    for i in 0..300000 {
        lines.push(format!("line {i} of the file, with some more text after it\n"));
    }
    fs::write("file-lines-rust.txt", lines.concat())?;
    let mut count: i64 = 0;
    let mut characters: i64 = 0;
    for line in BufReader::new(File::open("file-lines-rust.txt")?).lines() {
        count += 1;
        characters += line?.len() as i64;
    }
    fs::remove_file("file-lines-rust.txt")?;
    println!("{count} {characters}");
    Ok(())
}
