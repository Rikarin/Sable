// Port of benchmarks/versus-dotnet/text-builder.sb, with String.

use std::fmt::Write;

fn main() {
    let mut builder = String::new();
    for i in 0..1000000 {
        builder.push_str("item ");
        write!(builder, "{i}").unwrap();
        builder.push(';');
        if i % 10 == 0 {
            builder.push('\n');
        }
    }
    let built = builder.clone();

    for _ in 0..20 {
        builder.insert_str(builder.len() / 2, "<>");
    }
    builder.drain(..5);
    let edited = builder.replace("item", "it");
    println!("{} {}", std::hint::black_box(&built).len(), edited.len());
}
