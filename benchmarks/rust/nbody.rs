// Port of benchmarks/nbody.sb.

#[derive(Clone, Copy)]
struct Body {
    x: f64,
    y: f64,
    z: f64,
    vx: f64,
    vy: f64,
    vz: f64,
    mass: f64,
}

fn body(x: f64, y: f64, z: f64, vx: f64, vy: f64, vz: f64, mass: f64) -> Body {
    Body { x, y, z, vx, vy, vz, mass }
}

fn sqrt_approx(v: f64) -> f64 {
    let mut guess = v / 2.0 + 0.5;
    for _ in 0..12 {
        guess = 0.5 * (guess + v / guess);
    }
    guess
}

fn energy(bodies: &[Body]) -> f64 {
    let mut e = 0.0;
    for i in 0..bodies.len() {
        let b = bodies[i];
        e += 0.5 * b.mass * (b.vx * b.vx + b.vy * b.vy + b.vz * b.vz);
        for j in i + 1..bodies.len() {
            let c = bodies[j];
            let dx = b.x - c.x;
            let dy = b.y - c.y;
            let dz = b.z - c.z;
            e -= b.mass * c.mass / sqrt_approx(dx * dx + dy * dy + dz * dz);
        }
    }
    e
}

fn advance(bodies: &mut Vec<Body>, dt: f64) {
    let n = bodies.len();
    for i in 0..n {
        let mut b = bodies[i];
        for j in i + 1..n {
            let mut c = bodies[j];
            let dx = b.x - c.x;
            let dy = b.y - c.y;
            let dz = b.z - c.z;
            let d2 = dx * dx + dy * dy + dz * dz;
            let mag = dt / (d2 * sqrt_approx(d2));
            b.vx -= dx * c.mass * mag;
            b.vy -= dy * c.mass * mag;
            b.vz -= dz * c.mass * mag;
            c.vx += dx * b.mass * mag;
            c.vy += dy * b.mass * mag;
            c.vz += dz * b.mass * mag;
            bodies[j] = c;
        }
        b.x += dt * b.vx;
        b.y += dt * b.vy;
        b.z += dt * b.vz;
        bodies[i] = b;
    }
}

fn main() {
    let mut bodies = vec![
        body(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 39.47),
        body(4.84, -1.16, -0.10, 0.60, 2.81, -0.02, 0.037),
        body(8.34, 4.12, -0.40, -1.01, 1.82, 0.008, 0.011),
        body(12.89, -15.11, -0.22, 1.08, 0.86, -0.01, 0.0017),
        body(15.37, -25.91, 0.17, 0.97, 0.59, -0.03, 0.002),
    ];
    println!("{:?}", energy(&bodies));
    for _ in 0..200000 {
        advance(&mut bodies, 0.01);
    }
    println!("{:?}", energy(&bodies));
}
