// Port of byte-order.sb.

fn main() {
    let mut bytes = vec![0u8; 1048576];
    let mut total: u64 = 0;
    for round in 0..50usize {
        for at in (0..bytes.len()).step_by(16) {
            bytes[at..at + 4].copy_from_slice(&((at + round) as u32).to_be_bytes());
            bytes[at + 4..at + 12].copy_from_slice(&((at * 3) as u64).to_le_bytes());
        }
        for at in (0..bytes.len()).step_by(16) {
            let first = u32::from_be_bytes(bytes[at..at + 4].try_into().unwrap());
            let second = u64::from_le_bytes(bytes[at + 4..at + 12].try_into().unwrap());
            total = (total + first as u64 + second) % 1000000007;
        }
    }
    println!("{total}");
}
