using Geometry;

var r1 = new Rectangle(3.0, 4.0);
var r2 = new Rectangle(5.0, 6.0);
var total = Calc.TotalArea(r1, r2);
var p = r1.Perimeter();
System.Console.WriteLine(total + p);
