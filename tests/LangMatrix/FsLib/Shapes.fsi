module Geometry.Shapes

type IShape =
    abstract Area: unit -> double

type Rectangle =
    new: width: double * height: double -> Rectangle
    member Area: unit -> double
    member Perimeter: unit -> double
    interface IShape

module Calc =
    val TotalArea: a: IShape -> b: IShape -> double
