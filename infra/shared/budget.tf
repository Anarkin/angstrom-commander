# Detection, not prevention: Azure pay-as-you-go has no hard spending cap, so the
# protection against runaway cost is hearing about it fast and using the kill switch
# (README.md § Pausing an environment). Thresholds assume ~$55/month of expected spend
# (standing test stamp + shared resources); revisit the amount when qa/prod exist.

variable "alert_email" {
  type    = string
  default = "lengyel.adam@gmail.com"
}

data "azurerm_subscription" "current" {}

resource "azurerm_consumption_budget_subscription" "monthly" {
  name            = "angstrom-commander-monthly"
  subscription_id = data.azurerm_subscription.current.id
  amount          = 80
  time_grain      = "Monthly"

  time_period {
    # Must be the first of a month; the far-off end date just means "no planned end".
    start_date = "2026-08-01T00:00:00Z"
    end_date   = "2036-08-01T00:00:00Z"
  }

  notification {
    enabled        = true
    operator       = "GreaterThan"
    threshold      = 50
    threshold_type = "Actual"
    contact_emails = [var.alert_email]
  }

  notification {
    enabled        = true
    operator       = "GreaterThan"
    threshold      = 80
    threshold_type = "Actual"
    contact_emails = [var.alert_email]
  }

  notification {
    enabled        = true
    operator       = "GreaterThan"
    threshold      = 100
    threshold_type = "Actual"
    contact_emails = [var.alert_email]
  }

  # Trajectory alarm: fires when the projected month-end spend crosses the budget,
  # which catches a burn-rate spike days before the money is actually gone.
  notification {
    enabled        = true
    operator       = "GreaterThan"
    threshold      = 100
    threshold_type = "Forecasted"
    contact_emails = [var.alert_email]
  }
}
