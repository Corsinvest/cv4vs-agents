export interface IShape {
    area(): number;
}

export class Rectangle implements IShape {
    private readonly width: number;
    private readonly height: number;

    constructor(width: number, height: number) {
        this.width = width;
        this.height = height;
    }

    area(): number {
        return this.width * this.height;
    }

    perimeter(): number {
        return 2 * (this.width + this.height);
    }
}

export function totalArea(a: IShape, b: IShape): number {
    return a.area() + b.area();
}

const r1 = new Rectangle(3, 4);
const r2 = new Rectangle(5, 6);
export const result = totalArea(r1, r2) + r1.perimeter();
