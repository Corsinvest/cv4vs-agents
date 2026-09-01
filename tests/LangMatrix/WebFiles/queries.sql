CREATE TABLE Shapes (
    Id INT PRIMARY KEY,
    Width FLOAT NOT NULL,
    Height FLOAT NOT NULL
);

CREATE VIEW ShapeAreas AS
SELECT Id, Width * Height AS Area
FROM Shapes;

CREATE PROCEDURE GetTotalArea
AS
BEGIN
    SELECT SUM(Area) FROM ShapeAreas;
END;
