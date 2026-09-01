#include "shapes.h"

namespace geometry {

Rectangle::Rectangle(double w, double h) : width_(w), height_(h) {}

double Rectangle::Area() const {
    return width_ * height_;
}

double Rectangle::Perimeter() const {
    return 2.0 * (width_ + height_);
}

double TotalArea(const IShape& a, const IShape& b) {
    return a.Area() + b.Area();
}

}
