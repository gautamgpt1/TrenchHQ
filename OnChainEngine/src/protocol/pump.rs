use thiserror::Error;

use crate::domain::TradeDirection;
use curve25519_dalek::edwards::CompressedEdwardsY;
use sha2::{Digest, Sha256};

pub const PROGRAM_ID: &str = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P";
const WRAPPED_SOL_MINT: &str = "So11111111111111111111111111111111111111112";
const BONDING_CURVE_DISCRIMINATOR: [u8; 8] = [23, 183, 248, 55, 96, 216, 172, 96];
const TRADE_EVENT_DISCRIMINATOR: [u8; 8] = [189, 219, 127, 211, 78, 230, 97, 238];
const ANCHOR_CPI_EVENT_DISCRIMINATOR: [u8; 8] = [228, 69, 165, 46, 81, 203, 154, 29];
const PDA_MARKER: &[u8] = b"ProgramDerivedAddress";

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PumpBondingCurveState {
    pub virtual_token_reserves: u64,
    pub virtual_quote_reserves: u64,
    pub real_token_reserves: u64,
    pub real_quote_reserves: u64,
    pub token_total_supply: u64,
    pub complete: bool,
    pub creator: String,
    pub is_mayhem_mode: bool,
    pub is_cashback_coin: bool,
    pub quote_mint: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PumpTradeEvent {
    pub mint: String,
    pub quote_amount_raw: u64,
    pub token_amount_raw: u64,
    pub direction: TradeDirection,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum PumpDecodeError {
    #[error("account owner is not the Pump program")]
    WrongOwner,
    #[error("account is shorter than the supported Pump bonding-curve layout")]
    Truncated,
    #[error("account discriminator is not a Pump bonding curve")]
    WrongDiscriminator,
    #[error("boolean field contains {0} instead of 0 or 1")]
    InvalidBoolean(u8),
    #[error("unsupported Pump bonding-curve trailer length {0}")]
    UnsupportedTrailerLength(usize),
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum PumpTradeDecodeError {
    #[error("Pump TradeEvent is truncated")]
    Truncated,
    #[error("Pump TradeEvent boolean contains {0} instead of 0 or 1")]
    InvalidBoolean(u8),
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum PumpAddressError {
    #[error("mint is not a valid Solana public key")]
    InvalidMint,
    #[error("Pump program ID is invalid")]
    InvalidProgram,
    #[error("no valid Pump bonding-curve address exists for this mint")]
    NoValidAddress,
}

pub fn decode_bonding_curve(
    owner_program: &str,
    data: &[u8],
) -> Result<PumpBondingCurveState, PumpDecodeError> {
    if owner_program != PROGRAM_ID {
        return Err(PumpDecodeError::WrongOwner);
    }

    let mut reader = ByteReader::new(data);
    if reader.read_array::<8>()? != BONDING_CURVE_DISCRIMINATOR {
        return Err(PumpDecodeError::WrongDiscriminator);
    }

    let virtual_token_reserves = reader.read_u64()?;
    let virtual_quote_reserves = reader.read_u64()?;
    let real_token_reserves = reader.read_u64()?;
    let real_quote_reserves = reader.read_u64()?;
    let token_total_supply = reader.read_u64()?;
    let complete = reader.read_bool()?;
    let creator = encode_pubkey(reader.read_array::<32>()?);
    let (is_mayhem_mode, is_cashback_coin, quote_mint) = match reader.remaining() {
        0 => (false, false, WRAPPED_SOL_MINT.to_owned()),
        1 => (reader.read_bool()?, false, WRAPPED_SOL_MINT.to_owned()),
        34 => (
            reader.read_bool()?,
            reader.read_bool()?,
            encode_pubkey(reader.read_array::<32>()?),
        ),
        69 => {
            let is_mayhem_mode = reader.read_bool()?;
            let is_cashback_coin = reader.read_bool()?;
            if reader.read_array::<67>()? != [0; 67] {
                return Err(PumpDecodeError::UnsupportedTrailerLength(69));
            }
            (
                is_mayhem_mode,
                is_cashback_coin,
                WRAPPED_SOL_MINT.to_owned(),
            )
        }
        remaining => return Err(PumpDecodeError::UnsupportedTrailerLength(remaining)),
    };

    Ok(PumpBondingCurveState {
        virtual_token_reserves,
        virtual_quote_reserves,
        real_token_reserves,
        real_quote_reserves,
        token_total_supply,
        complete,
        creator,
        is_mayhem_mode,
        is_cashback_coin,
        quote_mint,
    })
}

fn encode_pubkey(bytes: [u8; 32]) -> String {
    bs58::encode(bytes).into_string()
}

pub fn decode_trade_event(data: &[u8]) -> Result<Option<PumpTradeEvent>, PumpTradeDecodeError> {
    let data = data
        .strip_prefix(&ANCHOR_CPI_EVENT_DISCRIMINATOR)
        .unwrap_or(data);
    if data.get(..8) != Some(TRADE_EVENT_DISCRIMINATOR.as_slice()) {
        return Ok(None);
    }

    let mint = data
        .get(8..40)
        .ok_or(PumpTradeDecodeError::Truncated)?
        .try_into()
        .map(encode_pubkey)
        .map_err(|_| PumpTradeDecodeError::Truncated)?;
    let quote_amount_raw = read_event_u64(data, 40)?;
    let token_amount_raw = read_event_u64(data, 48)?;
    let direction = match *data.get(56).ok_or(PumpTradeDecodeError::Truncated)? {
        0 => TradeDirection::Sell,
        1 => TradeDirection::Buy,
        value => return Err(PumpTradeDecodeError::InvalidBoolean(value)),
    };

    Ok(Some(PumpTradeEvent {
        mint,
        quote_amount_raw,
        token_amount_raw,
        direction,
    }))
}

pub fn derive_bonding_curve(mint: &str) -> Result<(String, u8), PumpAddressError> {
    let mint = decode_address(mint).ok_or(PumpAddressError::InvalidMint)?;
    let program = decode_address(PROGRAM_ID).ok_or(PumpAddressError::InvalidProgram)?;

    // Solana searches canonical bumps from 255 down to 1. A program-derived
    // address is valid only when the SHA-256 result is not an Ed25519 point.
    for bump in (1..=u8::MAX).rev() {
        let bump_seed = [bump];
        let mut hasher = Sha256::new();
        hasher.update(b"bonding-curve");
        hasher.update(mint);
        hasher.update(bump_seed);
        hasher.update(program);
        hasher.update(PDA_MARKER);
        let address: [u8; 32] = hasher.finalize().into();

        if CompressedEdwardsY(address).decompress().is_none() {
            return Ok((bs58::encode(address).into_string(), bump));
        }
    }

    Err(PumpAddressError::NoValidAddress)
}

fn decode_address(value: &str) -> Option<[u8; 32]> {
    let decoded = bs58::decode(value).into_vec().ok()?;
    decoded.try_into().ok()
}

fn read_event_u64(data: &[u8], offset: usize) -> Result<u64, PumpTradeDecodeError> {
    let bytes = data
        .get(offset..offset + 8)
        .ok_or(PumpTradeDecodeError::Truncated)?
        .try_into()
        .map_err(|_| PumpTradeDecodeError::Truncated)?;
    Ok(u64::from_le_bytes(bytes))
}

struct ByteReader<'a> {
    bytes: &'a [u8],
    position: usize,
}

impl<'a> ByteReader<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, position: 0 }
    }

    fn remaining(&self) -> usize {
        self.bytes.len().saturating_sub(self.position)
    }

    fn read_u64(&mut self) -> Result<u64, PumpDecodeError> {
        Ok(u64::from_le_bytes(self.read_array::<8>()?))
    }

    fn read_bool(&mut self) -> Result<bool, PumpDecodeError> {
        match self.read_array::<1>()?[0] {
            0 => Ok(false),
            1 => Ok(true),
            value => Err(PumpDecodeError::InvalidBoolean(value)),
        }
    }

    fn read_array<const N: usize>(&mut self) -> Result<[u8; N], PumpDecodeError> {
        let end = self
            .position
            .checked_add(N)
            .ok_or(PumpDecodeError::Truncated)?;
        let slice = self
            .bytes
            .get(self.position..end)
            .ok_or(PumpDecodeError::Truncated)?;
        self.position = end;
        slice.try_into().map_err(|_| PumpDecodeError::Truncated)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decodes_the_current_official_bonding_curve_layout() {
        let mut data = vec![23, 183, 248, 55, 96, 216, 172, 96];
        for value in [10_u64, 20, 30, 40, 50] {
            data.extend_from_slice(&value.to_le_bytes());
        }
        data.push(1);
        data.extend_from_slice(&[7_u8; 32]);
        data.push(0);
        data.push(1);
        data.extend_from_slice(&[9_u8; 32]);

        let state = decode_bonding_curve(PROGRAM_ID, &data).unwrap();
        assert_eq!(state.virtual_token_reserves, 10);
        assert_eq!(state.virtual_quote_reserves, 20);
        assert!(state.complete);
        assert!(!state.is_mayhem_mode);
        assert!(state.is_cashback_coin);
        assert_eq!(state.creator, bs58::encode([7_u8; 32]).into_string());
        assert_eq!(state.quote_mint, bs58::encode([9_u8; 32]).into_string());
    }

    #[test]
    fn decodes_the_observed_mayhem_only_legacy_layout_as_wsol_quote() {
        let mut data = BONDING_CURVE_DISCRIMINATOR.to_vec();
        for value in [10_u64, 20, 30, 40, 50] {
            data.extend_from_slice(&value.to_le_bytes());
        }
        data.push(0);
        data.extend_from_slice(&[7_u8; 32]);
        data.push(1);
        assert_eq!(data.len(), 82);

        let state = decode_bonding_curve(PROGRAM_ID, &data).unwrap();
        assert!(state.is_mayhem_mode);
        assert!(!state.is_cashback_coin);
        assert_eq!(state.quote_mint, WRAPPED_SOL_MINT);
    }

    #[test]
    fn decodes_the_current_padded_bonding_curve_allocation() {
        let mut data = BONDING_CURVE_DISCRIMINATOR.to_vec();
        for value in [10_u64, 20, 30, 40, 50] {
            data.extend_from_slice(&value.to_le_bytes());
        }
        data.push(0);
        data.extend_from_slice(&[7_u8; 32]);
        data.push(1);
        data.push(0);
        data.extend_from_slice(&[0; 67]);
        assert_eq!(data.len(), 150);

        let state = decode_bonding_curve(PROGRAM_ID, &data).unwrap();
        assert!(state.is_mayhem_mode);
        assert!(!state.is_cashback_coin);
        assert_eq!(state.quote_mint, WRAPPED_SOL_MINT);

        data[149] = 1;
        assert!(decode_bonding_curve(PROGRAM_ID, &data).is_err());
    }

    #[test]
    fn owner_validation_precedes_layout_use() {
        let error = decode_bonding_curve("wrong", &[]).unwrap_err();
        assert_eq!(error, PumpDecodeError::WrongOwner);
    }

    #[test]
    fn rejects_unknown_trailing_fields_instead_of_guessing() {
        let mut data = vec![23, 183, 248, 55, 96, 216, 172, 96];
        data.extend_from_slice(&[0_u8; 107]);
        data.push(1);

        let error = decode_bonding_curve(PROGRAM_ID, &data).unwrap_err();
        assert_eq!(error, PumpDecodeError::UnsupportedTrailerLength(35));
    }

    #[test]
    fn rejects_invalid_boolean_values() {
        let mut data = vec![23, 183, 248, 55, 96, 216, 172, 96];
        data.extend_from_slice(&[0_u8; 40]);
        data.push(2);
        data.extend_from_slice(&[0_u8; 66]);

        let error = decode_bonding_curve(PROGRAM_ID, &data).unwrap_err();
        assert_eq!(error, PumpDecodeError::InvalidBoolean(2));
    }

    #[test]
    fn decodes_trade_event_execution_amounts_without_assuming_later_optional_fields() {
        let mut data = TRADE_EVENT_DISCRIMINATOR.to_vec();
        data.extend_from_slice(&[3_u8; 32]);
        data.extend_from_slice(&2_000_000_000_u64.to_le_bytes());
        data.extend_from_slice(&1_000_000_u64.to_le_bytes());
        data.push(1);

        let event = decode_trade_event(&data).unwrap().unwrap();
        assert_eq!(event.mint, bs58::encode([3_u8; 32]).into_string());
        assert_eq!(event.quote_amount_raw, 2_000_000_000);
        assert_eq!(event.token_amount_raw, 1_000_000);
        assert_eq!(event.direction, TradeDirection::Buy);

        let mut cpi_event = ANCHOR_CPI_EVENT_DISCRIMINATOR.to_vec();
        cpi_event.extend_from_slice(&data);
        assert_eq!(decode_trade_event(&cpi_event).unwrap(), Some(event));
    }

    #[test]
    fn ignores_non_trade_event_instructions() {
        assert!(decode_trade_event(&[0_u8; 64]).unwrap().is_none());
    }

    #[test]
    fn derives_a_reproducible_off_curve_bonding_curve_address() {
        let mint = bs58::encode([3_u8; 32]).into_string();
        let (address, bump) = derive_bonding_curve(&mint).unwrap();
        assert_eq!(address, "BqZdLs4LGfrtY7fmwARY2LojpNfLm4LkeCV7cVmGBzGq");
        assert_eq!(bump, 252);
        let decoded: [u8; 32] = bs58::decode(address)
            .into_vec()
            .unwrap()
            .try_into()
            .unwrap();
        assert!(CompressedEdwardsY(decoded).decompress().is_none());
    }

    #[test]
    fn rejects_invalid_mint_addresses_before_derivation() {
        assert_eq!(
            derive_bonding_curve("not-a-solana-address").unwrap_err(),
            PumpAddressError::InvalidMint
        );
    }
}
