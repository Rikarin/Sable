// Port of benchmarks/versus-dotnet/list.sb.

fn main() {
    let mut total: i64 = 0;
    for round in 0..3i64 {
        let mut values: Vec<i64> = Vec::new();
        for i in 0..10000000i64 {
            values.push(i ^ round);
        }
        for v in &values {
            total += v;
        }
        let mut i = 0;
        while i < values.len() {
            total += values[i] % 7;
            i += 1;
        }
        while values.len() > 5000000 {
            total -= values.pop().unwrap();
        }
        total += values.len() as i64;
    }
    println!("{total}");
}
