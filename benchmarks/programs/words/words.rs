// Port of words.sb.

use std::collections::HashMap;

fn corpus(lines: usize) -> Vec<String> {
    let words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa"];
    let mut result = Vec::new();
    for i in 0..lines {
        let a = words[i % 10];
        let b = words[(i * 7 + 3) % 10];
        let c = words[(i * 13 + 5) % 10];
        result.push(format!("{a} {b} {c} {a}"));
    }
    result
}

#[derive(Clone)]
struct Count {
    word: String,
    count: i64,
}

fn ranked(items: &[Count], before: impl Fn(&Count, &Count) -> bool) -> Vec<Count> {
    let mut sorted = items.to_vec();
    for i in 1..sorted.len() {
        let mut j = i;
        while j > 0 && before(&sorted[j], &sorted[j - 1]) {
            sorted.swap(j, j - 1);
            j -= 1;
        }
    }
    sorted
}

fn main() {
    let mut counts: HashMap<String, i64> = HashMap::new();
    let mut seen: Vec<String> = Vec::new();
    for _ in 0..20 {
        for line in corpus(5000) {
            for word in line.split(' ') {
                if let Some(count) = counts.get_mut(word) {
                    *count += 1;
                } else {
                    seen.push(word.to_string());
                    counts.insert(word.to_string(), 1);
                }
            }
        }
    }

    let mut items = Vec::new();
    for word in seen {
        let count = counts.get(&word).copied().unwrap_or(0);
        items.push(Count { word, count });
    }
    for item in ranked(&items, |a, b| a.count > b.count) {
        println!("{} {}", item.word, item.count);
    }
}
