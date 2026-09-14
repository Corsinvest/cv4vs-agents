open Geometry.Shapes

let r1 = Rectangle(3.0, 4.0)
let r2 = Rectangle(5.0, 6.0)
let total = Calc.TotalArea r1 r2
let p = r1.Perimeter()
printfn "%f" (total + p)
