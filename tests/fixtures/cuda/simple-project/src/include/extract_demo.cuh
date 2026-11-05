#pragma once

// Target for the Extract Variable refactoring.
//
// Select the sub-expression on the return line and run Extract Variable: it
// lifts the expression onto a new statement above and replaces the selection
// with the variable.
namespace pythonsimple::extract_demo {

double total_price(double price, double qty);

} // namespace pythonsimple::extract_demo
