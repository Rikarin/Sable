// Port of shapes.sb: both dispatches are through trait objects,
// Rust having no classes.

trait Shape {
    fn area(&self) -> f64;
    fn name(&self) -> &'static str;
}

struct Circle {
    r: f64,
}

impl Shape for Circle {
    fn area(&self) -> f64 {
        3.14159 * self.r * self.r
    }
    fn name(&self) -> &'static str {
        "circle"
    }
}

struct Rect {
    w: f64,
    h: f64,
}

impl Shape for Rect {
    fn area(&self) -> f64 {
        self.w * self.h
    }
    fn name(&self) -> &'static str {
        "rect"
    }
}

trait Animal {
    fn legs(&self) -> i64;
    fn weight(&self) -> i64;
}

struct Bird {
    legs: i64,
}

impl Animal for Bird {
    fn legs(&self) -> i64 {
        self.legs
    }
    fn weight(&self) -> i64 {
        1
    }
}

struct Dog {
    legs: i64,
}

impl Animal for Dog {
    fn legs(&self) -> i64 {
        self.legs
    }
    fn weight(&self) -> i64 {
        20
    }
}

fn main() {
    let mut shapes: Vec<Box<dyn Shape>> = Vec::new();
    let mut animals: Vec<Box<dyn Animal>> = Vec::new();
    for i in 0..1000 {
        if i % 2 == 0 {
            shapes.push(Box::new(Circle { r: (i % 10) as f64 }));
            animals.push(Box::new(Bird { legs: 2 }));
        } else {
            shapes.push(Box::new(Rect { w: (i % 7) as f64, h: 2.0 }));
            animals.push(Box::new(Dog { legs: 4 }));
        }
    }

    let mut area = 0.0;
    let mut letters: i64 = 0;
    let mut weight: i64 = 0;
    for _ in 0..300 {
        for shape in &shapes {
            area += shape.area();
            letters += shape.name().len() as i64;
        }
        for animal in &animals {
            weight += animal.weight() + animal.legs();
        }
    }
    println!("{area:?}\n{letters}\n{weight}");
}
