#pragma once

namespace geometry {

class IShape {
public:
    virtual ~IShape() = default;
    virtual double Area() const = 0;
};

class Rectangle : public IShape {
public:
    Rectangle(double w, double h);
    double Area() const override;
    double Perimeter() const;
private:
    double width_;
    double height_;
};

double TotalArea(const IShape& a, const IShape& b);

}
