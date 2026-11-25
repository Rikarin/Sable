use std::ffi::{c_char, c_long, CString};

extern "C" {
    fn labs(n: c_long) -> c_long;
    fn strlen(text: *const c_char) -> usize;
}

fn main() {
    let mut total: c_long = 0;
    for i in 0..20_000_000 {
        total = total.wrapping_add(unsafe { labs(i - 10_000_000) });
    }
    let absolute: unsafe extern "C" fn(c_long) -> c_long = std::hint::black_box(labs);
    for i in 0..20_000_000 {
        total = total.wrapping_add(unsafe { absolute(i - 10_000_000) });
    }
    let mut length = 0;
    let text = "a name, as long as many are";
    for _ in 0..500_000 {
        let c = CString::new(std::hint::black_box(text)).unwrap();
        length += unsafe { strlen(c.as_ptr()) };
    }
    println!("{} {}", total, length);
}
