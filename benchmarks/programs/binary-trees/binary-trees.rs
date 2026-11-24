// Port of binary-trees.sb: a Box per node.

struct Node {
    left: Option<Box<Node>>,
    right: Option<Box<Node>>,
}

fn build(depth: i64) -> Box<Node> {
    if depth == 0 {
        return Box::new(Node { left: None, right: None });
    }
    Box::new(Node { left: Some(build(depth - 1)), right: Some(build(depth - 1)) })
}

fn check(node: &Node) -> i64 {
    let mut total = 1;
    if let Some(left) = &node.left {
        total += check(left);
    }
    if let Some(right) = &node.right {
        total += check(right);
    }
    total
}

fn main() {
    let max_depth: i64 = 16;
    let long_lived = build(max_depth);
    let mut depth = 4;
    while depth <= max_depth {
        let iterations: i64 = 1 << (max_depth - depth + 4);
        let mut sum = 0;
        for _ in 0..iterations {
            sum += check(&build(depth));
        }
        println!("{iterations} trees of depth {depth}: {sum}");
        depth += 2;
    }
    println!("long lived tree of depth {max_depth}: {}", check(&long_lived));
}
