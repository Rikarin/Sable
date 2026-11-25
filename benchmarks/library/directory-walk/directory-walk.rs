// Port of directory-walk.sb, with read_dir, as std has no walk.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

fn main() -> io::Result<()> {
    let root = std::env::current_dir()?.join("directory-walk-rust");
    let root = root.to_str().unwrap();
    for i in 0..10 {
        for j in 0..10 {
            let directory = format!("{root}/branch {i}/leaf {j}");
            fs::create_dir_all(&directory)?;
            for k in 0..20 {
                fs::write(format!("{directory}/file {k}.txt"), b"")?;
            }
        }
    }
    let mut files: i64 = 0;
    let mut directories: i64 = 0;
    let mut characters: i64 = 0;
    for _ in 0..20 {
        let mut waiting: Vec<PathBuf> = vec![Path::new(root).to_path_buf()];
        while let Some(directory) = waiting.pop() {
            for entry in fs::read_dir(&directory)? {
                let entry = entry?;
                let path = entry.path();
                characters += path.as_os_str().len() as i64 - root.len() as i64;
                if entry.file_type()?.is_dir() {
                    directories += 1;
                    waiting.push(path);
                } else {
                    files += 1;
                }
            }
        }
    }
    fs::remove_dir_all(root)?;
    println!("{files} {directories} {characters}");
    Ok(())
}
