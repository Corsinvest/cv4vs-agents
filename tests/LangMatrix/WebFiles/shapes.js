class Rectangle {
    constructor(width, height) {
        this.width = width;
        this.height = height;
    }

    area() {
        return this.width * this.height;
    }

    perimeter() {
        return 2 * (this.width + this.height);
    }
}

function totalArea(a, b) {
    return a.area() + b.area();
}

const r1 = new Rectangle(3, 4);
const r2 = new Rectangle(5, 6);
const result = totalArea(r1, r2) + r1.perimeter();
module.exports = { Rectangle, totalArea, result };
