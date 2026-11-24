// Port of benchmarks/versus-dotnet/string.sb. Words are slices of their
// lines, as str::split gives; the text is ASCII, so len() counts as Sable's
// count does.

fn main() {
    let names = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta"];
    let mut lines: Vec<String> = Vec::new();
    for i in 0..300000 {
        lines.push(format!("{} {i} {} end", names[i % 8], names[i * 3 % 8]));
    }

    let (mut characters, mut matches, mut words) = (0i64, 0i64, 0i64);
    for line in &lines {
        for word in line.split(' ') {
            words += 1;
            characters += word.len() as i64;
            if word == "gamma" {
                matches += 1;
            }
        }
    }

    let mut joined_length: i64 = 0;
    for i in (0..lines.len()).step_by(16) {
        let mut block = String::new();
        let mut j = 0;
        while j < 16 && i + j < lines.len() {
            block = block + &lines[i + j] + "\n";
            j += 1;
        }
        joined_length += block.len() as i64;
    }

    let mut sliced: i64 = 0;
    for line in &lines {
        if &line[..4] == "alph" {
            sliced += 1;
        }
    }
    println!("{words} {characters} {matches} {joined_length} {sliced}");
}
