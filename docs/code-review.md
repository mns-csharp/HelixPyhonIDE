# Code review

Code review checks Python code for issues that are difficult to capture with syntax errors or
linting alone. Each finding includes its location, severity, explanation, and a suggested change.

## Choose what to review

Open **Analysis** > **Code Review**, then choose one of these actions:

- **Review Whole Solution** checks Python files in the open solution.
- **Review Active File** checks the file open in the editor.
- **Review Uncommitted Changes** focuses on changed Python code and uses surrounding code as
  context.

## Read the result

The review panel shows a verdict and lists findings by severity and category. Select a finding to
read why it matters and how to address it. Open the finding's location to inspect the source.

An incomplete review identifies files or checks it could not process. Treat an incomplete result
as incomplete; it does not mean that the skipped code is clean.

Use **Export Last Review** to save the report as Markdown.
