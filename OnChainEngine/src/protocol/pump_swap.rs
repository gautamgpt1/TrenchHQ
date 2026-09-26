use thiserror::Error;

use crate::domain::TradeDirection;

pub const PROGRAM_ID: &str = "pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA";
const POOL_DISCRIMINATOR: [u8; 8] = [241, 154, 109, 4, 17, 177, 109, 188];
const BUY_EVENT_DISCRIMINATOR: [u8; 8] = [103, 244, 82, 31, 44, 245, 119, 119];
const SELL_EVENT_DISCRIMINATOR: [u8; 8] = [62, 47, 55, 10, 165, 3, 220, 42];
const ANCHOR_CPI_EVENT_DISCRIMINATOR: [u8; 8] = [228, 69, 165, 46, 81, 203, 154, 29];

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PoolLayout {
    Legacy,
    VirtualQuoteReserves,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PumpSwapPoolState {
    pub layout: PoolLayout,
    pub pool_bump: u8,
    pub index: u16,
    pub creator: String,
    pub base_mint: String,
    pub quote_mint: String,
    pub lp_mint: String,
    pub pool_base_token_account: String,
    pub pool_quote_token_account: String,
    pub lp_supply: u64,
    pub coin_creator: String,
    pub is_mayhem_mode: bool,
    pub is_cashback_coin: bool,
    pub virtual_quote_reserves: i128,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PumpSwapTradeEvent {
    pub pool: String,
    pub base_amount_raw: u64,
    pub quote_amount_raw: u64,
    pub direction: TradeDirection,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum PumpSwapDecodeError {
    #[error("account owner is not the PumpSwap program")]
    WrongOwner,
    #[error("account is shorter than the supported PumpSwap Pool layout")]
    Truncated,
    #[error("account discriminator is not a PumpSwap Pool")]
    WrongDiscriminator,
    #[error("boolean field contains {0} instead of 0 or 1")]
    InvalidBoolean(u8),
    #[error("unsupported PumpSwap Pool trailer length {0}")]
    UnsupportedTrailerLength(usize),
    #[error("PumpSwap Pool reserved padding contains non-zero data")]
    NonZeroReservedPadding,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum PumpSwapTradeDecodeError {
    #[error("PumpSwap trade event is truncated")]
    Truncated,
}

pub fn decode_pool(
    owner_program: &str,
    data: &[u8],
) -> Result<PumpSwapPoolState, PumpSwapDecodeError> {
    if owner_program != PROGRAM_ID {
        return Err(PumpSwapDecodeError::WrongOwner);
    }

    let mut reader = ByteReader::new(data);
    if reader.read_array::<8>()? != POOL_DISCRIMINATOR {
        return Err(PumpSwapDecodeError::WrongDiscriminator);
    }

    let pool_bump = reader.read_u8()?;
    let index = reader.read_u16()?;
    let creator = encode_pubkey(reader.read_array::<32>()?);
    let base_mint = encode_pubkey(reader.read_array::<32>()?);
    let quote_mint = encode_pubkey(reader.read_array::<32>()?);
    let lp_mint = encode_pubkey(reader.read_array::<32>()?);
    let pool_base_token_account = encode_pubkey(reader.read_array::<32>()?);
    let pool_quote_token_account = encode_pubkey(reader.read_array::<32>()?);
    let lp_supply = reader.read_u64()?;
    let coin_creator = encode_pubkey(reader.read_array::<32>()?);
    let is_mayhem_mode = reader.read_bool()?;
    let is_cashback_coin = reader.read_bool()?;

    let (layout, virtual_quote_reserves) = match reader.remaining() {
        0 => (PoolLayout::Legacy, 0),
        16 => (PoolLayout::VirtualQuoteReserves, reader.read_i128()?),
        55 => {
            let virtual_quote_reserves = reader.read_i128()?;
            if reader.read_array::<39>()? != [0; 39] {
                return Err(PumpSwapDecodeError::NonZeroReservedPadding);
            }
            (PoolLayout::VirtualQuoteReserves, virtual_quote_reserves)
        }
        56 => {
            let virtual_quote_reserves = reader.read_i128()?;
            if reader.read_array::<40>()? != [0; 40] {
                return Err(PumpSwapDecodeError::NonZeroReservedPadding);
            }
            (PoolLayout::VirtualQuoteReserves, virtual_quote_reserves)
        }
        remaining => return Err(PumpSwapDecodeError::UnsupportedTrailerLength(remaining)),
    };

    Ok(PumpSwapPoolState {
        layout,
        pool_bump,
        index,
        creator,
        base_mint,
        quote_mint,
        lp_mint,
        pool_base_token_account,
        pool_quote_token_account,
        lp_supply,
        coin_creator,
        is_mayhem_mode,
        is_cashback_coin,
        virtual_quote_reserves,
    })
}

fn encode_pubkey(bytes: [u8; 32]) -> String {
    bs58::encode(bytes).into_string()
}

pub fn decode_trade_event(
    data: &[u8],
) -> Result<Option<PumpSwapTradeEvent>, PumpSwapTradeDecodeError> {
    let data = data
        .strip_prefix(&ANCHOR_CPI_EVENT_DISCRIMINATOR)
        .unwrap_or(data);
    let direction = match data.get(..8) {
        Some(discriminator) if discriminator == BUY_EVENT_DISCRIMINATOR => TradeDirection::Buy,
        Some(discriminator) if discriminator == SELL_EVENT_DISCRIMINATOR => TradeDirection::Sell,
        _ => return Ok(None),
    };

    let base_amount_raw = read_event_u64(data, 16)?;
    let quote_amount_raw = read_event_u64(data, 112)?;
    let pool = data
        .get(120..152)
        .ok_or(PumpSwapTradeDecodeError::Truncated)?
        .try_into()
        .map(encode_pubkey)
        .map_err(|_| PumpSwapTradeDecodeError::Truncated)?;

    Ok(Some(PumpSwapTradeEvent {
        pool,
        base_amount_raw,
        quote_amount_raw,
        direction,
    }))
}

fn read_event_u64(data: &[u8], offset: usize) -> Result<u64, PumpSwapTradeDecodeError> {
    let bytes = data
        .get(offset..offset + 8)
        .ok_or(PumpSwapTradeDecodeError::Truncated)?
        .try_into()
        .map_err(|_| PumpSwapTradeDecodeError::Truncated)?;
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

    fn read_u8(&mut self) -> Result<u8, PumpSwapDecodeError> {
        Ok(self.read_array::<1>()?[0])
    }

    fn read_bool(&mut self) -> Result<bool, PumpSwapDecodeError> {
        match self.read_u8()? {
            0 => Ok(false),
            1 => Ok(true),
            value => Err(PumpSwapDecodeError::InvalidBoolean(value)),
        }
    }

    fn read_u16(&mut self) -> Result<u16, PumpSwapDecodeError> {
        Ok(u16::from_le_bytes(self.read_array::<2>()?))
    }

    fn read_u64(&mut self) -> Result<u64, PumpSwapDecodeError> {
        Ok(u64::from_le_bytes(self.read_array::<8>()?))
    }

    fn read_i128(&mut self) -> Result<i128, PumpSwapDecodeError> {
        Ok(i128::from_le_bytes(self.read_array::<16>()?))
    }

    fn read_array<const N: usize>(&mut self) -> Result<[u8; N], PumpSwapDecodeError> {
        let end = self
            .position
            .checked_add(N)
            .ok_or(PumpSwapDecodeError::Truncated)?;
        let slice = self
            .bytes
            .get(self.position..end)
            .ok_or(PumpSwapDecodeError::Truncated)?;
        self.position = end;
        slice.try_into().map_err(|_| PumpSwapDecodeError::Truncated)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn pool_bytes(virtual_quote_reserves: Option<i128>) -> Vec<u8> {
        let mut data = POOL_DISCRIMINATOR.to_vec();
        data.push(254);
        data.extend_from_slice(&42_u16.to_le_bytes());
        for seed in 1_u8..=6 {
            data.extend_from_slice(&[seed; 32]);
        }
        data.extend_from_slice(&123_456_u64.to_le_bytes());
        data.extend_from_slice(&[7_u8; 32]);
        data.push(1);
        data.push(0);
        if let Some(value) = virtual_quote_reserves {
            data.extend_from_slice(&value.to_le_bytes());
        }
        data
    }

    #[test]
    fn decodes_the_current_i128_virtual_quote_reserve_trailer() {
        let data = pool_bytes(Some(987_654_321));
        assert_eq!(data.len(), 261);

        let pool = decode_pool(PROGRAM_ID, &data).unwrap();
        assert_eq!(pool.layout, PoolLayout::VirtualQuoteReserves);
        assert_eq!(pool.index, 42);
        assert_eq!(pool.lp_supply, 123_456);
        assert_eq!(pool.virtual_quote_reserves, 987_654_321);
        assert!(pool.is_mayhem_mode);
        assert!(!pool.is_cashback_coin);
    }

    #[test]
    fn decodes_the_live_padded_pool_allocation_when_reserved_bytes_are_zero() {
        let mut data = pool_bytes(Some(987_654_321));
        data.extend_from_slice(&[0; 39]);
        assert_eq!(data.len(), 300);

        let pool = decode_pool(PROGRAM_ID, &data).unwrap();
        assert_eq!(pool.layout, PoolLayout::VirtualQuoteReserves);
        assert_eq!(pool.virtual_quote_reserves, 987_654_321);
    }

    #[test]
    fn decodes_the_expanded_live_padded_pool_allocation_when_reserved_bytes_are_zero() {
        let mut data = pool_bytes(Some(987_654_321));
        data.extend_from_slice(&[0; 40]);
        assert_eq!(data.len(), 301);

        let pool = decode_pool(PROGRAM_ID, &data).unwrap();
        assert_eq!(pool.layout, PoolLayout::VirtualQuoteReserves);
        assert_eq!(pool.virtual_quote_reserves, 987_654_321);
    }

    #[test]
    fn rejects_non_zero_data_in_the_live_pool_reserved_allocation() {
        let mut data = pool_bytes(Some(0));
        data.extend_from_slice(&[0; 40]);
        data[300] = 1;

        let error = decode_pool(PROGRAM_ID, &data).unwrap_err();
        assert_eq!(error, PumpSwapDecodeError::NonZeroReservedPadding);
    }

    #[test]
    fn legacy_layout_is_explicit_and_defaults_only_the_absent_trailer() {
        let data = pool_bytes(None);
        assert_eq!(data.len(), 245);

        let pool = decode_pool(PROGRAM_ID, &data).unwrap();
        assert_eq!(pool.layout, PoolLayout::Legacy);
        assert_eq!(pool.virtual_quote_reserves, 0);
    }

    #[test]
    fn unknown_trailing_layout_is_rejected_instead_of_guessed() {
        let mut data = pool_bytes(None);
        data.push(0);
        let error = decode_pool(PROGRAM_ID, &data).unwrap_err();
        assert_eq!(error, PumpSwapDecodeError::UnsupportedTrailerLength(1));
    }

    #[test]
    fn owner_is_validated() {
        let error = decode_pool("wrong", &pool_bytes(Some(0))).unwrap_err();
        assert_eq!(error, PumpSwapDecodeError::WrongOwner);
    }

    #[test]
    fn decodes_buy_event_using_the_user_executed_quote_amount() {
        let mut data = vec![0_u8; 152];
        data[..8].copy_from_slice(&BUY_EVENT_DISCRIMINATOR);
        data[16..24].copy_from_slice(&1_000_000_u64.to_le_bytes());
        data[64..72].copy_from_slice(&1_900_000_000_u64.to_le_bytes());
        data[112..120].copy_from_slice(&2_000_000_000_u64.to_le_bytes());
        data[120..152].copy_from_slice(&[4_u8; 32]);

        let event = decode_trade_event(&data).unwrap().unwrap();
        assert_eq!(event.base_amount_raw, 1_000_000);
        assert_eq!(event.quote_amount_raw, 2_000_000_000);
        assert_eq!(event.direction, TradeDirection::Buy);
        assert_eq!(event.pool, bs58::encode([4_u8; 32]).into_string());

        let mut cpi_event = ANCHOR_CPI_EVENT_DISCRIMINATOR.to_vec();
        cpi_event.extend_from_slice(&data);
        assert_eq!(decode_trade_event(&cpi_event).unwrap(), Some(event));
    }

    #[test]
    fn decodes_sell_event_direction() {
        let mut data = vec![0_u8; 152];
        data[..8].copy_from_slice(&SELL_EVENT_DISCRIMINATOR);
        data[16..24].copy_from_slice(&1_u64.to_le_bytes());
        data[112..120].copy_from_slice(&2_u64.to_le_bytes());
        data[120..152].copy_from_slice(&[5_u8; 32]);

        let event = decode_trade_event(&data).unwrap().unwrap();
        assert_eq!(event.direction, TradeDirection::Sell);
    }
}
