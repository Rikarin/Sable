// Port of sort.sb, with std's stable sort; the
// strings are sorted as references.

fn main() {
    let mut numbers: Vec<i64> = Vec::new();
    let mut x: i64 = 42;
    for _ in 0..1000000 {
        x = (x * 1103515245 + 12345) % 2147483648;
        numbers.push(x);
    }
    let mut words: Vec<String> = Vec::new();
    for _ in 0..200000 {
        x = (x * 1103515245 + 12345) % 2147483648;
        words.push(format!("w{}", x % 1000000));
    }

    let mut a = numbers.clone();
    a.sort();
    let mut b: Vec<&str> = words.iter().map(|word| word.as_str()).collect();
    b.sort();

    let mut ordered = true;
    for i in 1..a.len() {
        if a[i] < a[i - 1] {
            ordered = false;
        }
    }
    for i in 1..b.len() {
        if b[i] < b[i - 1] {
            ordered = false;
        }
    }
    println!("{ordered} {} {} {} {}", a[0], a[a.len() / 2], b[0], b[b.len() - 1]);
}
