// Port of file-metadata.sb.

use std::fs::{self, File};
use std::io::{self, Write};
use std::path::Path;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

fn main() -> io::Result<()> {
    let root = std::env::current_dir()?.join("file-metadata-rust");
    let root = root.to_str().unwrap();
    fs::create_dir(root)?;
    let mut paths: Vec<String> = Vec::new();
    for i in 0..200usize {
        let path = format!("{root}/file {i}.txt");
        let mut file = File::create(&path)?;
        file.write_all(&vec![1u8; i])?;
        let time: SystemTime = UNIX_EPOCH + Duration::from_secs(1700000000 + i as u64);
        file.set_modified(time)?;
        paths.push(path);
    }
    let mut size: u64 = 0;
    let mut seconds: u64 = 0;
    let mut files: u64 = 0;
    for _ in 0..200 {
        for path in &paths {
            let metadata = fs::metadata(path)?;
            size += metadata.len();
            seconds += metadata.modified()?.duration_since(UNIX_EPOCH).unwrap().as_secs() - 1700000000;
            if metadata.is_file() {
                files += 1;
            }
            if Path::new(&format!("{path}.gone")).exists() {
                files += 1;
            }
        }
    }
    fs::remove_dir_all(root)?;
    println!("{size} {seconds} {files}");
    Ok(())
}
