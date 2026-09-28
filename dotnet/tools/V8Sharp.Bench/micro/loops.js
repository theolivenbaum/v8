// Dispatch cost: loops of 100000 iterations with one kind of operation.
bench('EmptyLoop', function () { var s = 0; for (var i = 0; i < 100000; i++) { } return s; });
bench('AddLoop', function () { var s = 0; for (var i = 0; i < 100000; i++) { s = s + i; } return s; });
bench('BitOrLoop', function () { var s = 0; for (var i = 0; i < 100000; i++) { s = (s + i) | 0; } return s; });
bench('MulLoop', function () { var s = 0; for (var i = 0; i < 100000; i++) { s = (s + i * 3) | 0; } return s; });
bench('XorShiftLoop', function () { var s = 0; for (var i = 0; i < 100000; i++) { s = s ^ (i >> 1); } return s; });
