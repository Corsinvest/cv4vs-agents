Namespace Geometry

    Public Interface IShape
        Function Area() As Double
    End Interface

    Public Class Rectangle
        Implements IShape

        Private ReadOnly _width As Double
        Private ReadOnly _height As Double

        Public Sub New(width As Double, height As Double)
            _width = width
            _height = height
        End Sub

        Public Function Area() As Double Implements IShape.Area
            Return _width * _height
        End Function

        Public Function Perimeter() As Double
            Return 2.0 * (_width + _height)
        End Function
    End Class

    Public Module Calc
        Public Function TotalArea(a As IShape, b As IShape) As Double
            Return a.Area() + b.Area()
        End Function
    End Module

End Namespace
