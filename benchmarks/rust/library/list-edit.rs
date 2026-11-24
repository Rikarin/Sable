// Port of benchmarks/versus-dotnet/list-edit.sb.

fn main() {
    let mut total: i64 = 0;
    for round in 0..3i64 {
        let mut list: Vec<i64> = Vec::new();
        for i in 0..1000000i64 {
            list.push(i ^ round);
        }
        let mut i = 0;
        while i < list.len() {
            total += list[i] % 7;
            i += 1;
        }
        list.truncate(20000);
        for i in 0..2000i64 {
            list.insert(list.len() / 2, i);
        }
        for _ in 0..2000 {
            total += list.remove(list.len() / 3);
        }
        list.retain(|x| x % 2 != 0);
        total += list.len() as i64 + list[list.len() / 2];
    }
    println!("{total}");
}
