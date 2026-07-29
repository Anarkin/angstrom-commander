import { render, screen } from "@testing-library/react";
import App from "./App";

test("shows the product name", () => {
  render(<App />);

  expect(
    screen.getByRole("heading", { name: /angstrom commander/i }),
  ).toBeInTheDocument();
});

test("shows the configured server", () => {
  render(<App />);

  expect(screen.getByText(/http:\/\/localhost:5080/)).toBeInTheDocument();
});
