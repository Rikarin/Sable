// Port of stack.sb: Rust's stack is Vec.

#[path = "../../support/rust/bench.rs"]
mod bench;

fn main() {
    bench::run(|| {
        let mut stack: Vec<i64> = Vec::new();
        let mut total: i64 = 0;
        for i in 0..1000 {
            stack.push(i);
        }
        for i in 0..5000000 {
            stack.push(i);
            total += stack.pop().unwrap();
        }
        for i in 0..2000000 {
            stack.push(i);
        }
        while let Some(value) = stack.pop() {
            total += value;
        }
        format!("{total}")
    });
}
