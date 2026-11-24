// Port of map-string.sb: the map's keys are
// references to the list's strings, shared as Sable's and .NET's are.

use std::collections::HashMap;

fn main() {
    let n = 200000;
    let mut keys: Vec<String> = Vec::new();
    for i in 0..n as i64 {
        keys.push(format!("key-{}", i * 7919 % 1000003));
    }

    let mut map: HashMap<&str, i64> = HashMap::new();
    for i in 0..n {
        map.insert(&keys[i], i as i64);
    }

    let mut sum: i64 = 0;
    for _ in 0..5 {
        for key in &keys {
            sum += map.get(key.as_str()).copied().unwrap_or(0);
        }
    }

    for i in (0..n).step_by(2) {
        map.remove(keys[i].as_str());
    }

    let mut hits: i64 = 0;
    for key in &keys {
        if map.contains_key(key.as_str()) {
            hits += 1;
        }
    }
    println!("{} {sum} {hits}", map.len());
}
