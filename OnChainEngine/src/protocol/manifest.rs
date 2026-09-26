use crate::domain::TradeDirection;
use thiserror::Error;

pub const PROGRAM_ID: &str = "MNFSTqtC93rEfYHB6hF82sKdZpUDFWkViLByLd1k1Ms";
pub const MARKET_FIXED_SIZE: usize = 256;
pub const MARKET_BLOCK_SIZE: usize = 80;
pub const MARKET_FIXED_DISCRIMINANT: u64 = 4_859_840_929_024_028_656;
pub const FILL_LOG_DISCRIMINANT: [u8; 8] = [58, 230, 242, 3, 75, 113, 4, 169];
const FILL_LOG_SIZE: usize = 232;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ManifestMarketState {
    pub base_mint: String,
    pub quote_mint: String,
    pub base_vault: String,
    pub quote_vault: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ManifestFill {
    pub market: String,
    pub base_mint: String,
    pub quote_mint: String,
    pub base_amount_raw: u64,
    pub quote_amount_raw: u64,
    pub direction: TradeDirection,
}

#[derive(Debug, Error)]
pub enum ManifestDecodeError {
    #[error("account owner is not the Manifest program")]
    WrongOwner,
    #[error("account length is not a complete Manifest market allocation")]
    WrongLength,
    #[error("account discriminator is not a Manifest market")]
    WrongDiscriminator,
    #[error("Manifest market version is unsupported")]
    UnsupportedVersion,
    #[error("Manifest market contains an invalid mint or vault")]
    InvalidIdentity,
    #[error("Manifest fill log layout is malformed")]
    InvalidFill,
}

pub fn decode_market(
    owner_program: &str,
    data: &[u8],
) -> Result<ManifestMarketState, ManifestDecodeError> {
    if owner_program != PROGRAM_ID {
        return Err(ManifestDecodeError::WrongOwner);
    }
    if data.len() < MARKET_FIXED_SIZE
        || !(data.len() - MARKET_FIXED_SIZE).is_multiple_of(MARKET_BLOCK_SIZE)
    {
        return Err(ManifestDecodeError::WrongLength);
    }
    if read_u64(data, 0) != Some(MARKET_FIXED_DISCRIMINANT) {
        return Err(ManifestDecodeError::WrongDiscriminator);
    }
    if data[8] != 0 {
        return Err(ManifestDecodeError::UnsupportedVersion);
    }
    if read_u32(data, 152)
        .map(usize::try_from)
        .and_then(Result::ok)
        != Some(data.len() - MARKET_FIXED_SIZE)
    {
        return Err(ManifestDecodeError::WrongLength);
    }

    let base_mint = read_pubkey(data, 16).ok_or(ManifestDecodeError::InvalidIdentity)?;
    let quote_mint = read_pubkey(data, 48).ok_or(ManifestDecodeError::InvalidIdentity)?;
    let base_vault = read_pubkey(data, 80).ok_or(ManifestDecodeError::InvalidIdentity)?;
    let quote_vault = read_pubkey(data, 112).ok_or(ManifestDecodeError::InvalidIdentity)?;
    if base_mint == quote_mint || base_vault == quote_vault {
        return Err(ManifestDecodeError::InvalidIdentity);
    }

    Ok(ManifestMarketState {
        base_mint,
        quote_mint,
        base_vault,
        quote_vault,
    })
}

pub fn decode_fill(data: &[u8]) -> Result<Option<ManifestFill>, ManifestDecodeError> {
    if data.len() < FILL_LOG_DISCRIMINANT.len() || data[..8] != FILL_LOG_DISCRIMINANT {
        return Ok(None);
    }
    if data.len() != FILL_LOG_SIZE || data[216] > 1 || data[217] > 1 {
        return Err(ManifestDecodeError::InvalidFill);
    }
    let market = read_pubkey(data, 8).ok_or(ManifestDecodeError::InvalidFill)?;
    let base_mint = read_pubkey(data, 104).ok_or(ManifestDecodeError::InvalidFill)?;
    let quote_mint = read_pubkey(data, 136).ok_or(ManifestDecodeError::InvalidFill)?;
    let base_amount_raw = read_u64(data, 184).ok_or(ManifestDecodeError::InvalidFill)?;
    let quote_amount_raw = read_u64(data, 192).ok_or(ManifestDecodeError::InvalidFill)?;
    if base_mint == quote_mint || base_amount_raw == 0 || quote_amount_raw == 0 {
        return Err(ManifestDecodeError::InvalidFill);
    }
    Ok(Some(ManifestFill {
        market,
        base_mint,
        quote_mint,
        base_amount_raw,
        quote_amount_raw,
        direction: if data[216] == 1 {
            TradeDirection::Buy
        } else {
            TradeDirection::Sell
        },
    }))
}

fn read_pubkey(data: &[u8], offset: usize) -> Option<String> {
    let bytes: [u8; 32] = data.get(offset..offset.checked_add(32)?)?.try_into().ok()?;
    (bytes != [0; 32]).then(|| bs58::encode(bytes).into_string())
}

fn read_u64(data: &[u8], offset: usize) -> Option<u64> {
    data.get(offset..offset.checked_add(8)?)?
        .try_into()
        .ok()
        .map(u64::from_le_bytes)
}

fn read_u32(data: &[u8], offset: usize) -> Option<u32> {
    data.get(offset..offset.checked_add(4)?)?
        .try_into()
        .ok()
        .map(u32::from_le_bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decodes_strict_market_header_and_fill_log() {
        let base = [3_u8; 32];
        let quote = [4_u8; 32];
        let base_vault = [5_u8; 32];
        let quote_vault = [6_u8; 32];
        let market = [7_u8; 32];
        let mut market_data = vec![0_u8; MARKET_FIXED_SIZE + MARKET_BLOCK_SIZE];
        market_data[..8].copy_from_slice(&MARKET_FIXED_DISCRIMINANT.to_le_bytes());
        market_data[16..48].copy_from_slice(&base);
        market_data[48..80].copy_from_slice(&quote);
        market_data[80..112].copy_from_slice(&base_vault);
        market_data[112..144].copy_from_slice(&quote_vault);
        market_data[152..156].copy_from_slice(&(MARKET_BLOCK_SIZE as u32).to_le_bytes());

        let decoded = decode_market(PROGRAM_ID, &market_data).unwrap();
        assert_eq!(decoded.base_mint, bs58::encode(base).into_string());
        assert_eq!(decoded.quote_vault, bs58::encode(quote_vault).into_string());

        let mut fill = vec![0_u8; FILL_LOG_SIZE];
        fill[..8].copy_from_slice(&FILL_LOG_DISCRIMINANT);
        fill[8..40].copy_from_slice(&market);
        fill[104..136].copy_from_slice(&base);
        fill[136..168].copy_from_slice(&quote);
        fill[184..192].copy_from_slice(&1_000_000_u64.to_le_bytes());
        fill[192..200].copy_from_slice(&2_000_000_000_u64.to_le_bytes());
        fill[216] = 1;
        let decoded = decode_fill(&fill).unwrap().unwrap();
        assert_eq!(decoded.market, bs58::encode(market).into_string());
        assert_eq!(decoded.direction, TradeDirection::Buy);
        assert_eq!(decoded.base_amount_raw, 1_000_000);
        assert_eq!(decoded.quote_amount_raw, 2_000_000_000);
    }

    #[test]
    fn rejects_similar_accounts_and_malformed_fill_logs() {
        let mut data = vec![0_u8; MARKET_FIXED_SIZE];
        data[..8].copy_from_slice(&MARKET_FIXED_DISCRIMINANT.to_le_bytes());
        data[16..48].copy_from_slice(&[3; 32]);
        data[48..80].copy_from_slice(&[4; 32]);
        data[80..112].copy_from_slice(&[5; 32]);
        data[112..144].copy_from_slice(&[6; 32]);
        assert!(matches!(
            decode_market("wrong", &data),
            Err(ManifestDecodeError::WrongOwner)
        ));
        data[8] = 1;
        assert!(matches!(
            decode_market(PROGRAM_ID, &data),
            Err(ManifestDecodeError::UnsupportedVersion)
        ));

        let mut fill = vec![0_u8; FILL_LOG_SIZE];
        fill[..8].copy_from_slice(&FILL_LOG_DISCRIMINANT);
        fill[216] = 2;
        assert!(matches!(
            decode_fill(&fill),
            Err(ManifestDecodeError::InvalidFill)
        ));
        assert_eq!(decode_fill(&[1, 2, 3]).unwrap(), None);
    }
}
