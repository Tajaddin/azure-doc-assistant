import { expect, test } from "@playwright/test";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const invoice = path.resolve(here, "../../data/synthetic/invoices/INV-1001.pdf");
const apiBase = process.env.API_BASE ?? "http://localhost:8080";
const user = `e2e-${Date.now()}`;

test("sign in, upload, ask with a citation, extract fields, delete", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("test user").fill(user);
  await page.getByRole("button", { name: "Sign in (development)" }).click();
  await expect(page.getByTestId("signed-in")).toContainText(user);

  await page.getByTestId("file-input").setInputFiles(invoice);
  const row = page.getByTestId("doc-row").filter({ hasText: "INV-1001.pdf" });
  await expect(row).toHaveAttribute("data-status", "Ready", { timeout: 90_000 });
  await expect(row).toContainText("Invoice");

  await page.getByTestId("question").fill("What is the total amount due on invoice INV-1001?");
  await page.getByTestId("ask").click();
  const turn = page.getByTestId("turn").last();
  await expect(turn.getByTestId("answer")).not.toHaveText("", { timeout: 90_000 });
  await expect(turn.getByTestId("citations")).toContainText("INV-1001.pdf");

  await row.getByRole("button", { name: "Extract fields" }).click();
  await expect(page.getByTestId("extraction")).toContainText("INV-1001");

  await row.getByRole("button", { name: "delete INV-1001.pdf" }).click();
  await expect(row).toHaveCount(0);
});

test("unauthenticated API calls are rejected and the page still renders", async ({ page, request }) => {
  const resp = await request.get(`${apiBase}/api/documents`);
  expect(resp.status()).toBe(401);
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Document assistant" })).toBeVisible();
});
