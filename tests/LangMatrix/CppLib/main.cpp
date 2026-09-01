#include "shapes.h"

int main() {
    geometry::Rectangle r1(3.0, 4.0);
    geometry::Rectangle r2(5.0, 6.0);
    double total = geometry::TotalArea(r1, r2);
    double p = r1.Perimeter();
    return total > p ? 0 : 1;
}
