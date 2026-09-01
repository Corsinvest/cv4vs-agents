namespace Geometry;

public interface IShape
{
    double Area();
}

public class Rectangle : IShape
{
    private readonly double _width;
    private readonly double _height;

    public Rectangle(double width, double height)
    {
        _width = width;
        _height = height;
    }

    public double Area() => _width * _height;

    public double Perimeter() => 2.0 * (_width + _height);
}

public static class Calc
{
    public static double TotalArea(IShape a, IShape b) => a.Area() + b.Area();
}
