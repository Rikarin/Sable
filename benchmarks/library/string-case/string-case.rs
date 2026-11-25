// Port of string-case.sb.

#[path = "../../support/rust/bench.rs"]
mod bench;

fn main() {
    let mut ascii: Vec<String> = Vec::new();
    let mut mixed: Vec<String> = Vec::new();
    for i in 0..20000 {
        ascii.push(format!("The Quick Brown Fox {i} Jumps Over The Lazy Dog"));
        mixed.push(format!("Привет Мир {i} Καλημερα Κοσμε and plain words too"));
    }

    bench::run(|| {
        let mut total: i64 = 0;
        for _ in 0..5 {
            for text in ascii.iter().chain(&mixed) {
                if text.to_uppercase() != *text {
                    total += 1;
                }
                total += text.to_lowercase().chars().count() as i64;
            }
        }
        format!("{total}")
    });
}
