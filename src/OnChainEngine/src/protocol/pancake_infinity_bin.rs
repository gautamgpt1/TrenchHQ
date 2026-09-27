use crate::domain::DecimalValue;
use crate::price::{
    dlmm_active_bin_spot_price, rational_quote_per_base, PriceMathError, DEFAULT_PRICE_SCALE,
};
use num_bigint::{BigInt, BigUint, Sign};
use thiserror::Error;

pub const PROTOCOL_ID: &str = "pancake-infinity-bin";
pub const BNB_POOL_MANAGER: &str = "0xc697d2898e0d09264376196696c51d7abbbaa4a9";
pub const SWAP_TOPIC: &str = "0x3e8aae37f890eb1f9d63dd4d2062f3f0be757848a0f0760e4f3e53dad556e861";
pub const SLOT0_CALL_ID: &str = "slot0";
pub const ACTIVE_ID_OFFSET: i32 = 1 << 23;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Swap {
    pub pool_id: String,
    pub amount0: BigInt,
    pub amount1: BigInt,
    pub active_id: u32,
    pub fee: u32,
    pub protocol_fee: u16,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Slot0 {
    pub active_id: u32,
    pub protocol_fee: u32,
    pub lp_fee: u32,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum DecodeError {
    #[error("PancakeSwap Infinity bin swap topics are malformed")]
    InvalidTopics,
    #[error("PancakeSwap Infinity bin ABI data is malformed")]
    InvalidData,
    #[error("PancakeSwap Infinity bin swap deltas are zero or have the same sign")]
    AmbiguousSwap,
    #[error(transparent)]
    Price(#[from] PriceMathError),
}

pub fn decode_swap(topics: &[String], data: &str) -> Result<Swap, DecodeError> {
    if topics.len() != 3
        || !topics[0].eq_ignore_ascii_case(SWAP_TOPIC)
        || !crate::protocol::uniswap_v4::is_pool_id(&topics[1])
    {
        return Err(DecodeError::InvalidTopics);
    }
    let words = decode_words(data, 5)?;
    if words[2].bits() > 24 || words[3].bits() > 24 || words[4].bits() > 16 {
        return Err(DecodeError::InvalidData);
    }
    Ok(Swap {
        pool_id: topics[1].to_ascii_lowercase(),
        amount0: signed_small(&words[0], 128)?,
        amount1: signed_small(&words[1], 128)?,
        active_id: to_u32(&words[2])?,
        fee: to_u32(&words[3])?,
        protocol_fee: to_u16(&words[4])?,
    })
}

pub fn decode_slot0(data: &str) -> Result<Slot0, DecodeError> {
    let words = decode_words(data, 3)?;
    if words.iter().any(|word| word.bits() > 24) {
        return Err(DecodeError::InvalidData);
    }
    Ok(Slot0 {
        active_id: to_u32(&words[0])?,
        protocol_fee: to_u32(&words[1])?,
        lp_fee: to_u32(&words[2])?,
    })
}

pub fn executed_price(
    swap: &Swap,
    selected_is_token0: bool,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, DecodeError> {
    if swap.amount0 == BigInt::from(0)
        || swap.amount1 == BigInt::from(0)
        || swap.amount0.sign() == swap.amount1.sign()
    {
        return Err(DecodeError::AmbiguousSwap);
    }
    let (base, quote) = if selected_is_token0 {
        (
            swap.amount0.magnitude().clone(),
            swap.amount1.magnitude().clone(),
        )
    } else {
        (
            swap.amount1.magnitude().clone(),
            swap.amount0.magnitude().clone(),
        )
    };
    Ok(rational_quote_per_base(
        quote,
        base,
        base_decimals,
        quote_decimals,
        DEFAULT_PRICE_SCALE,
    )?)
}

pub fn spot_price(
    active_id: u32,
    bin_step: u16,
    selected_is_token0: bool,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, DecodeError> {
    if active_id >= 1 << 24 || bin_step == 0 {
        return Err(DecodeError::InvalidData);
    }
    Ok(dlmm_active_bin_spot_price(
        active_id as i32 - ACTIVE_ID_OFFSET,
        bin_step,
        selected_is_token0,
        base_decimals,
        quote_decimals,
    )?)
}

fn signed_small(word: &BigUint, bits: u32) -> Result<BigInt, DecodeError> {
    let value = if word.bit(255) {
        BigInt::from_biguint(Sign::Plus, word.clone()) - (BigInt::from(1) << 256)
    } else {
        BigInt::from_biguint(Sign::Plus, word.clone())
    };
    let limit = BigInt::from(1) << (bits - 1);
    if value < -&limit || value >= limit {
        return Err(DecodeError::InvalidData);
    }
    Ok(value)
}

fn to_u32(value: &BigUint) -> Result<u32, DecodeError> {
    value
        .to_string()
        .parse()
        .map_err(|_| DecodeError::InvalidData)
}

fn to_u16(value: &BigUint) -> Result<u16, DecodeError> {
    value
        .to_string()
        .parse()
        .map_err(|_| DecodeError::InvalidData)
}

fn decode_words(data: &str, count: usize) -> Result<Vec<BigUint>, DecodeError> {
    if data.len() != 2 + count * 64
        || !data.starts_with("0x")
        || !data.as_bytes()[2..]
            .iter()
            .all(|byte| byte.is_ascii_hexdigit())
    {
        return Err(DecodeError::InvalidData);
    }
    (0..count)
        .map(|index| {
            let start = 2 + index * 64;
            BigUint::parse_bytes(&data.as_bytes()[start..start + 64], 16)
                .ok_or(DecodeError::InvalidData)
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn word(value: u32) -> String {
        format!("{value:0>64x}")
    }

    #[test]
    fn live_slot0_fixture_and_active_bin_price_decode() {
        let data = concat!(
            "0x0000000000000000000000000000000000000000000000000000000000800001",
            "0000000000000000000000000000000000000000000000000000000000001001",
            "0000000000000000000000000000000000000000000000000000000000000004"
        );
        let slot0 = decode_slot0(data).unwrap();
        assert_eq!(slot0.active_id, 8_388_609);
        assert_eq!(slot0.protocol_fee, 4_097);
        assert_eq!(slot0.lp_fee, 4);
        assert_eq!(
            spot_price(slot0.active_id, 1, true, 18, 18)
                .unwrap()
                .coefficient,
            "1000100000000000000"
        );
    }

    #[test]
    fn five_word_swap_rejects_same_sign_deltas() {
        let data = format!(
            "0x{}{}{}{}{}",
            word(1),
            word(2),
            word(1 << 23),
            word(4),
            word(0)
        );
        let topics = vec![
            SWAP_TOPIC.to_owned(),
            format!("0x{:064x}", 7),
            format!("0x{:064x}", 8),
        ];
        let swap = decode_swap(&topics, &data).unwrap();
        assert_eq!(
            executed_price(&swap, true, 18, 18),
            Err(DecodeError::AmbiguousSwap)
        );
    }
}
