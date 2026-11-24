// Port of linked-list.sb.

use std::collections::LinkedList;

fn main() {
    let mut list: LinkedList<i64> = LinkedList::new();
    for i in 0..500000 {
        list.push_back(i);
    }
    let mut total: i64 = 0;
    for _ in 0..10 {
        for value in &list {
            total += value;
        }
    }
    for _ in 0..2000000 {
        let value = list.pop_front().unwrap();
        total += value;
        list.push_back(value);
    }
    println!("{} {total}", list.len());
}
