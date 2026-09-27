use crate::domain::DecimalValue;
use crate::price::{
    rational_quote_per_base, sqrt_price_x96_spot_price, PriceMathError, DEFAULT_PRICE_SCALE,
};
use num_bigint::{BigInt, BigUint, Sign};
use thiserror::Error;

pub const PROTOCOL_ID: &str = "pancake-infinity-cl";
pub const BNB_POOL_MANAGER: &str = "0xa0ffb9c1ce1fe56963b0321b32e7a0302114058b";
pub const SWAP_TOPIC: &str = "0x04206ad2b7c0f463bff3dd4f33c5735b0f2957a351e4f79763a4fa9e775dd237";
pub const SLOT0_CALL_ID: &str = "slot0";
pub const LIQUIDITY_CALL_ID: &str = "liquidity";

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Swap {
    pub pool_id: String,
    pub amount0: BigInt,
    pub amount1: BigInt,
    pub sqrt_price_x96: BigUint,
    pub liquidity: BigUint,
    pub tick: i32,
    pub fee: u32,
    pub protocol_fee: u16,
}

pub type Slot0 = crate::protocol::uniswap_v4::Slot0;

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum DecodeError {
    #[error("PancakeSwap Infinity CL swap topics are malformed")]
    InvalidTopics,
    #[error("PancakeSwap Infinity CL ABI data is malformed")]
    InvalidData,
    #[error("PancakeSwap Infinity CL swap deltas are zero or have the same sign")]
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
    let words = decode_words(data, 7)?;
    let amount0 = signed_small(&words[0], 128)?;
    let amount1 = signed_small(&words[1], 128)?;
    let sqrt_price_x96 = words[2].clone();
    let liquidity = words[3].clone();
    let tick = to_i32(signed_small(&words[4], 24)?)?;
    if sqrt_price_x96 == BigUint::from(0_u8)
        || sqrt_price_x96.bits() > 160
        || liquidity.bits() > 128
        || !(-887_272..=887_272).contains(&tick)
        || words[5].bits() > 24
        || words[6].bits() > 16
    {
        return Err(DecodeError::InvalidData);
    }
    Ok(Swap {
        pool_id: topics[1].to_ascii_lowercase(),
        amount0,
        amount1,
        sqrt_price_x96,
        liquidity,
        tick,
        fee: to_u32(&words[5])?,
        protocol_fee: to_u16(&words[6])?,
    })
}

pub fn decode_slot0(data: &str) -> Result<Slot0, DecodeError> {
    crate::protocol::uniswap_v4::decode_slot0(data).map_err(|_| DecodeError::InvalidData)
}

pub fn decode_liquidity(data: &str) -> Result<BigUint, DecodeError> {
    crate::protocol::uniswap_v4::decode_liquidity(data).map_err(|_| DecodeError::InvalidData)
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
    sqrt_price_x96: &BigUint,
    selected_is_token0: bool,
    base_decimals: u8,
    quote_decimals: u8,
) -> Result<DecimalValue, DecodeError> {
    Ok(sqrt_price_x96_spot_price(
        sqrt_price_x96.clone(),
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

fn to_i32(value: BigInt) -> Result<i32, DecodeError> {
    value
        .to_string()
        .parse()
        .map_err(|_| DecodeError::InvalidData)
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

    #[test]
    fn live_bnb_swap_fixture_decodes() {
        let topics = vec![
            SWAP_TOPIC.to_owned(),
            "0x32a805e65a3219a79707ab3e75443d75160d798de46613fc960d39cd4a96bb22".to_owned(),
            "0x00000000000000000000000022ec88b9ff78c6f2458ab1a7aa8bb99d84bd4b86".to_owned(),
        ];
        let data = "0xffffffffffffffffffffffffffffffffffffffffffffffc9ca36523a2160000000000000000000000000000000000000000000000000003637a88fea572f5e06000000000000000000000000000000000000000100046ee78d888038c096b721000000000000000000000000000000000000000002283830641cc75d89e8b5b9000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000001";
        let swap = decode_swap(&topics, data).unwrap();
        assert_eq!(swap.pool_id, topics[1]);
        assert_eq!(swap.tick, 1);
        assert_eq!(swap.fee, 2);
        assert_eq!(swap.protocol_fee, 1);
        assert_eq!(swap.liquidity.to_string(), "667592397607891450328561081");
    }
}
