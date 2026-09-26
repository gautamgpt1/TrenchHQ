use crate::domain::DecimalValue;
use num_bigint::BigUint;
use std::str::FromStr;
use thiserror::Error;

pub const DEFAULT_PRICE_SCALE: u32 = 18;

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum PriceMathError {
    #[error("base reserve or executed base amount is zero")]
    ZeroBaseAmount,
    #[error("effective quote reserves are not positive")]
    NonPositiveEffectiveQuoteReserve,
    #[error("effective quote reserve overflowed i128")]
    EffectiveQuoteReserveOverflow,
    #[error("pool fees exceed the corresponding vault balance")]
    FeesExceedVaultBalance,
    #[error("square-root price is zero")]
    ZeroSqrtPrice,
    #[error("DLMM active-bin price is outside the supported Q64.64 range")]
    InvalidDlmmPrice,
    #[error("decimal coefficient is not a non-negative integer")]
    InvalidDecimal,
    #[error("decimal value must be positive")]
    NonPositiveDecimal,
}

pub fn quote_per_base(
    quote_raw: BigUint,
    base_raw: BigUint,
    base_decimals: u8,
    quote_decimals: u8,
    output_scale: u32,
) -> Result<DecimalValue, PriceMathError> {
    rational_quote_per_base(
        quote_raw,
        base_raw,
        base_decimals,
        quote_decimals,
        output_scale,
    )
}

pub fn rational_quote_per_base(
    raw_quote_numerator: BigUint,
    raw_base_denominator: BigUint,
    base_decimals: u8,
    quote_decimals: u8,
    output_scale: u32,
) -> Result<DecimalValue, PriceMathError> {
    if raw_base_denominator == BigUint::from(0_u8) {
        return Err(PriceMathError::ZeroBaseAmount);
    }

    let numerator = raw_quote_numerator * ten_to(u32::from(base_decimals) + output_scale);
    let denominator = raw_base_denominator * ten_to(u32::from(quote_decimals));
    let quotient = &numerator / &denominator;
    let remainder = numerator % &denominator;
    let rounded = if remainder * BigUint::from(2_u8) >= denominator {
        quotient + BigUint::from(1_u8)
    } else {
        quotient
    };

    Ok(DecimalValue {
        coefficient: rounded.to_str_radix(10),
        scale: output_scale,
    })
}

#[allow(clippy::too_many_arguments)]
pub fn constant_product_spot_price(
    quote_vault_amount: u64,
    quote_protocol_fees: u64,
    quote_fund_fees: u64,
    quote_creator_fees: u64,
    base_vault_amount: u64,
    base_protocol_fees: u64,
    base_fund_fees: u64,
    base_creator_fees: u64,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, PriceMathError> {
    let effective_quote = subtract_fees(
        quote_vault_amount,
        quote_protocol_fees,
        quote_fund_fees,
        quote_creator_fees,
    )?;
    let effective_base = subtract_fees(
        base_vault_amount,
        base_protocol_fees,
        base_fund_fees,
        base_creator_fees,
    )?;
    quote_per_base(
        BigUint::from(effective_quote),
        BigUint::from(effective_base),
        base_decimals,
        quote_decimals,
        DEFAULT_PRICE_SCALE,
    )
}

pub fn sqrt_price_x64_spot_price(
    sqrt_price_x64: u128,
    selected_is_token_0: bool,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, PriceMathError> {
    if sqrt_price_x64 == 0 {
        return Err(PriceMathError::ZeroSqrtPrice);
    }
    let squared = BigUint::from(sqrt_price_x64).pow(2);
    let q128 = BigUint::from(1_u8) << 128;
    let (numerator, denominator) = if selected_is_token_0 {
        (squared, q128)
    } else {
        (q128, squared)
    };
    rational_quote_per_base(
        numerator,
        denominator,
        base_decimals,
        quote_decimals,
        DEFAULT_PRICE_SCALE,
    )
}

pub fn sqrt_price_x96_spot_price(
    sqrt_price_x96: BigUint,
    selected_is_token_0: bool,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, PriceMathError> {
    if sqrt_price_x96 == BigUint::from(0_u8) {
        return Err(PriceMathError::ZeroSqrtPrice);
    }
    let squared = sqrt_price_x96.pow(2);
    let q192 = BigUint::from(1_u8) << 192;
    let (numerator, denominator) = if selected_is_token_0 {
        (squared, q192)
    } else {
        (q192, squared)
    };
    rational_quote_per_base(
        numerator,
        denominator,
        base_decimals,
        quote_decimals,
        DEFAULT_PRICE_SCALE,
    )
}

pub fn dlmm_active_bin_spot_price(
    active_id: i32,
    bin_step: u16,
    selected_is_token_x: bool,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, PriceMathError> {
    const Q64_ONE: u128 = 1_u128 << 64;
    const MAX_EXPONENT: u32 = 0x80000;

    let exponent = active_id.unsigned_abs();
    if exponent >= MAX_EXPONENT {
        return Err(PriceMathError::InvalidDlmmPrice);
    }

    let basis_points = u128::from(bin_step)
        .checked_shl(64)
        .and_then(|value| value.checked_div(10_000))
        .ok_or(PriceMathError::InvalidDlmmPrice)?;
    let mut factor = Q64_ONE
        .checked_add(basis_points)
        .ok_or(PriceMathError::InvalidDlmmPrice)?;
    let mut invert = active_id.is_negative();

    // Keep every multiplication inside Q64.64. Since the base is above one,
    // exponentiate its reciprocal and invert only once at the end.
    if factor >= Q64_ONE {
        factor = u128::MAX
            .checked_div(factor)
            .ok_or(PriceMathError::InvalidDlmmPrice)?;
        invert = !invert;
    }

    let mut result = Q64_ONE;
    let mut remaining = exponent;
    while remaining > 0 {
        if remaining & 1 == 1 {
            result = result
                .checked_mul(factor)
                .map(|value| value >> 64)
                .filter(|value| *value > 0)
                .ok_or(PriceMathError::InvalidDlmmPrice)?;
        }
        remaining >>= 1;
        if remaining > 0 {
            factor = factor
                .checked_mul(factor)
                .map(|value| value >> 64)
                .filter(|value| *value > 0)
                .ok_or(PriceMathError::InvalidDlmmPrice)?;
        }
    }

    if invert {
        result = u128::MAX
            .checked_div(result)
            .filter(|value| *value > 0)
            .ok_or(PriceMathError::InvalidDlmmPrice)?;
    }

    let (numerator, denominator) = if selected_is_token_x {
        (BigUint::from(result), BigUint::from(Q64_ONE))
    } else {
        (BigUint::from(Q64_ONE), BigUint::from(result))
    };
    rational_quote_per_base(
        numerator,
        denominator,
        base_decimals,
        quote_decimals,
        DEFAULT_PRICE_SCALE,
    )
}

pub fn pump_curve_spot_price(
    virtual_quote_reserves: u64,
    virtual_token_reserves: u64,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, PriceMathError> {
    quote_per_base(
        BigUint::from(virtual_quote_reserves),
        BigUint::from(virtual_token_reserves),
        base_decimals,
        quote_decimals,
        DEFAULT_PRICE_SCALE,
    )
}

pub fn pump_swap_spot_price(
    quote_vault_amount: u64,
    virtual_quote_reserves: i128,
    base_vault_amount: u64,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, PriceMathError> {
    let effective_quote = i128::from(quote_vault_amount)
        .checked_add(virtual_quote_reserves)
        .ok_or(PriceMathError::EffectiveQuoteReserveOverflow)?;

    if effective_quote <= 0 {
        return Err(PriceMathError::NonPositiveEffectiveQuoteReserve);
    }

    quote_per_base(
        BigUint::from(effective_quote as u128),
        BigUint::from(base_vault_amount),
        base_decimals,
        quote_decimals,
        DEFAULT_PRICE_SCALE,
    )
}

pub fn executed_trade_price(
    quote_amount_raw: u64,
    base_amount_raw: u64,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, PriceMathError> {
    quote_per_base(
        BigUint::from(quote_amount_raw),
        BigUint::from(base_amount_raw),
        base_decimals,
        quote_decimals,
        DEFAULT_PRICE_SCALE,
    )
}

pub fn multiply_decimal(
    left: &DecimalValue,
    right: &DecimalValue,
) -> Result<DecimalValue, PriceMathError> {
    let left_coefficient = parse_decimal(left)?;
    let right_coefficient = parse_decimal(right)?;
    if left_coefficient == BigUint::from(0_u8) || right_coefficient == BigUint::from(0_u8) {
        return Err(PriceMathError::NonPositiveDecimal);
    }
    Ok(DecimalValue {
        coefficient: (left_coefficient * right_coefficient).to_str_radix(10),
        scale: left.scale.saturating_add(right.scale),
    })
}

fn parse_decimal(value: &DecimalValue) -> Result<BigUint, PriceMathError> {
    BigUint::from_str(&value.coefficient).map_err(|_| PriceMathError::InvalidDecimal)
}

fn subtract_fees(
    vault: u64,
    protocol: u64,
    fund: u64,
    creator: u64,
) -> Result<u64, PriceMathError> {
    let fees = protocol
        .checked_add(fund)
        .and_then(|value| value.checked_add(creator))
        .ok_or(PriceMathError::FeesExceedVaultBalance)?;
    vault
        .checked_sub(fees)
        .ok_or(PriceMathError::FeesExceedVaultBalance)
}

fn ten_to(exponent: u32) -> BigUint {
    BigUint::from(10_u8).pow(exponent)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decimal_orientation_is_quote_units_per_base_unit() {
        let price = executed_trade_price(2_000_000_000, 1_000_000, 6, 9).unwrap();
        assert_eq!(price.coefficient, "2000000000000000000");
        assert_eq!(price.scale, 18);
    }

    #[test]
    fn pump_swap_includes_signed_virtual_quote_reserves() {
        let price = pump_swap_spot_price(1_000_000_000, 500_000_000, 1_000_000, 6, 9).unwrap();
        assert_eq!(price.coefficient, "1500000000000000000");
    }

    #[test]
    fn pump_swap_allows_negative_virtual_reserves_when_effective_reserves_stay_positive() {
        let price = pump_swap_spot_price(1_000_000_000, -500_000_000, 1_000_000, 6, 9).unwrap();
        assert_eq!(price.coefficient, "500000000000000000");
    }

    #[test]
    fn pump_swap_rejects_non_positive_effective_reserves() {
        let error = pump_swap_spot_price(1_000, -1_000, 1, 0, 0).unwrap_err();
        assert_eq!(error, PriceMathError::NonPositiveEffectiveQuoteReserve);
    }

    #[test]
    fn zero_base_amount_is_rejected() {
        let error = executed_trade_price(1, 0, 6, 9).unwrap_err();
        assert_eq!(error, PriceMathError::ZeroBaseAmount);
    }

    #[test]
    fn division_rounds_half_up_at_the_requested_scale() {
        let price = quote_per_base(BigUint::from(2_u8), BigUint::from(3_u8), 0, 0, 2).unwrap();
        assert_eq!(price.coefficient, "67");
        assert_eq!(price.scale, 2);
    }

    #[test]
    fn exact_decimal_multiplication_preserves_all_input_scale() {
        let token_sol = DecimalValue {
            coefficient: "2000000000000000000".to_owned(),
            scale: 18,
        };
        let sol_usd = DecimalValue {
            coefficient: "15025".to_owned(),
            scale: 2,
        };
        let usd = multiply_decimal(&token_sol, &sol_usd).unwrap();
        assert_eq!(usd.coefficient, "30050000000000000000000");
        assert_eq!(usd.scale, 20);
    }

    #[test]
    fn decimal_multiplication_rejects_zero_reference_prices() {
        let error = multiply_decimal(
            &DecimalValue {
                coefficient: "1".to_owned(),
                scale: 0,
            },
            &DecimalValue {
                coefficient: "0".to_owned(),
                scale: 0,
            },
        )
        .unwrap_err();
        assert_eq!(error, PriceMathError::NonPositiveDecimal);
    }

    #[test]
    fn cpmm_spot_excludes_all_fee_accumulators() {
        let price = constant_product_spot_price(
            2_300_000_000,
            100_000_000,
            100_000_000,
            100_000_000,
            1_300_000,
            100_000,
            100_000,
            100_000,
            6,
            9,
        )
        .unwrap();
        assert_eq!(price.coefficient, "2000000000000000000");
    }

    #[test]
    fn q64_spot_handles_both_orientations_and_decimal_adjustment() {
        let token_1_per_token_0 = sqrt_price_x64_spot_price(1_u128 << 64, true, 6, 9).unwrap();
        assert_eq!(token_1_per_token_0.coefficient, "1000000000000000");
        let token_0_per_token_1 = sqrt_price_x64_spot_price(1_u128 << 64, false, 9, 6).unwrap();
        assert_eq!(token_0_per_token_1.coefficient, "1000000000000000000000");
    }

    #[test]
    fn dlmm_active_bin_uses_bin_step_exponent_and_orientation() {
        let at_zero = dlmm_active_bin_spot_price(0, 25, true, 6, 9).unwrap();
        assert_eq!(at_zero.coefficient, "1000000000000000");
        assert_eq!(at_zero.scale, DEFAULT_PRICE_SCALE);

        let forward = dlmm_active_bin_spot_price(10, 100, true, 6, 6).unwrap();
        let inverse = dlmm_active_bin_spot_price(10, 100, false, 6, 6).unwrap();
        let forward_value: f64 = format!("{}e-{}", forward.coefficient, forward.scale)
            .parse()
            .unwrap();
        let inverse_value: f64 = format!("{}e-{}", inverse.coefficient, inverse.scale)
            .parse()
            .unwrap();
        assert!((forward_value * inverse_value - 1.0).abs() < 1e-12);
    }

    #[test]
    fn dlmm_active_bin_rejects_values_outside_the_protocol_q64_range() {
        let error = dlmm_active_bin_spot_price(0x80000, 1, true, 6, 6).unwrap_err();
        assert_eq!(error, PriceMathError::InvalidDlmmPrice);
    }
}
