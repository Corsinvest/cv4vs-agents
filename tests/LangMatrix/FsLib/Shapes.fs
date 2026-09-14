module Geometry.Shapes

type IShape =
    abstract Area: unit -> double

type Rectangle(width: double, height: double) =
    member _.Area() = width * height
    member _.Perimeter() = 2.0 * (width + height)
    interface IShape with
        member this.Area() = this.Area()

module Calc =
    let TotalArea (a: IShape) (b: IShape) = a.Area() + b.Area()
