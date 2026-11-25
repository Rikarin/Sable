use std::ffi::c_void;

extern "C" {
    fn qsort(base: *mut i32, count: usize, size: usize, compare: extern "C" fn(*const i32, *const i32) -> i32);
}

extern "C" fn by_value(a: *const i32, b: *const i32) -> i32 {
    unsafe { (*a).cmp(&*b) as i32 }
}

extern "C" fn step<F: FnMut(i32) -> i32>(n: i32, context: *mut c_void) -> i32 {
    unsafe { (*(context as *mut F))(n) }
}

fn entry<F: FnMut(i32) -> i32>(_: &F) -> extern "C" fn(i32, *mut c_void) -> i32 {
    step::<F>
}

fn main() {
    let mut numbers = Vec::new();
    let mut seed: u32 = 12345;
    for _ in 0..300_000 {
        seed = seed.wrapping_mul(1664525).wrapping_add(1013904223);
        numbers.push((seed >> 1) as i32);
    }
    unsafe { qsort(numbers.as_mut_ptr(), numbers.len(), 4, by_value) };
    let mut total: i64 = 0;
    let mut add = |n: i32| {
        total += n as i64;
        n + 1
    };
    let call = std::hint::black_box(entry(&add));
    let context = &mut add as *mut _ as *mut c_void;
    let mut k = 0;
    while k < 1_000_000 {
        k = call(k, context);
    }
    println!("{} {} {} {}", numbers[0], numbers[150_000], numbers[299_999], total);
}
