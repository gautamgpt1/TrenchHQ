use crate::domain::DecimalValue;
use crate::protocol::uniswap_v3;
use num_bigint::BigUint;

pub const PROTOCOL_ID: &str = "aerodrome-slipstream";
pub const BASE_INITIAL_FACTORY: &str = "0x5e7bb104d84c7cb9b682aac2f3d509f5f406809a";
pub const BASE_GAUGE_CAPS_FACTORY: &str = "0xade65c38cd4849adba595a4323a8c7ddfe89716a";
pub const BASE_GAUGES_V3_FACTORY: &str = "0xf8f2eb4940cfe7d13603dddd87f123820fc061ef";
pub const SWAP_TOPIC: &str = uniswap_v3::SWAP_TOPIC;
pub const SLOT0_CALL_ID: &str = uniswap_v3::SLOT0_CALL_ID;
pub const LIQUIDITY_CALL_ID: &str = uniswap_v3::LIQUIDITY_CALL_ID;

pub use uniswap_v3::{DecodeError, Slot0, Swap};

pub fn decode_swap(topics: &[String], data: &str) -> Result<Swap, DecodeError> {
    uniswap_v3::decode_swap(topics, data)
}

pub fn decode_slot0(data: &str) -> Result<Slot0, DecodeError> {
    uniswap_v3::decode_slot0_words(data, 6)
}

pub fn decode_liquidity(data: &str) -> Result<BigUint, DecodeError> {
    uniswap_v3::decode_liquidity(data)
}

pub fn executed_price(
    swap: &Swap,
    selected_is_token0: bool,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, DecodeError> {
    uniswap_v3::executed_price(swap, selected_is_token0, base_decimals, quote_decimals)
}

pub fn spot_price(
    sqrt_price_x96: &BigUint,
    selected_is_token0: bool,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, DecodeError> {
    uniswap_v3::spot_price(
        sqrt_price_x96,
        selected_is_token0,
        base_decimals,
        quote_decimals,
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn word(value: u128) -> String {
        format!("{value:064x}")
    }

    #[test]
    fn slipstream_slot0_requires_exactly_six_words() {
        let six_words = format!(
            "0x{}{}{}{}{}{}",
            word(1_u128 << 96),
            word(0),
            word(0),
            word(0),
            word(0),
            word(1)
        );
        assert_eq!(decode_slot0(&six_words).unwrap().tick, 0);
        assert_eq!(
            uniswap_v3::decode_slot0(&six_words),
            Err(DecodeError::InvalidData)
        );
        assert_eq!(
            decode_slot0(&(six_words + &word(0))),
            Err(DecodeError::InvalidData)
        );
    }
}
