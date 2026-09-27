use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::io::{self, Read, Write};
use thiserror::Error;

pub const PROTOCOL_NAME: &str = "trenchhq.onchain";
pub const PROTOCOL_VERSION: u32 = 2;
pub const MAX_FRAME_BYTES: usize = 4 * 1024 * 1024;

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Envelope {
    pub protocol: String,
    pub protocol_version: u32,
    pub message_type: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub request_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sequence: Option<u64>,
    #[serde(default)]
    pub payload: Value,
}

impl Envelope {
    pub fn request(
        message_type: impl Into<String>,
        request_id: impl Into<String>,
        payload: Value,
    ) -> Self {
        Self {
            protocol: PROTOCOL_NAME.to_owned(),
            protocol_version: PROTOCOL_VERSION,
            message_type: message_type.into(),
            request_id: Some(request_id.into()),
            sequence: None,
            payload,
        }
    }

    pub fn response(
        message_type: impl Into<String>,
        request_id: Option<String>,
        payload: Value,
    ) -> Self {
        Self {
            protocol: PROTOCOL_NAME.to_owned(),
            protocol_version: PROTOCOL_VERSION,
            message_type: message_type.into(),
            request_id,
            sequence: None,
            payload,
        }
    }

    pub fn event(message_type: impl Into<String>, sequence: Option<u64>, payload: Value) -> Self {
        Self {
            protocol: PROTOCOL_NAME.to_owned(),
            protocol_version: PROTOCOL_VERSION,
            message_type: message_type.into(),
            request_id: None,
            sequence,
            payload,
        }
    }
}

#[derive(Debug, Error)]
pub enum FrameError {
    #[error("frame I/O failed: {0}")]
    Io(#[from] io::Error),
    #[error("frame length cannot be zero")]
    EmptyFrame,
    #[error("frame length {actual} exceeds the {maximum}-byte limit")]
    FrameTooLarge { actual: usize, maximum: usize },
    #[error("frame JSON is invalid: {0}")]
    Json(#[from] serde_json::Error),
}

pub fn read_frame<R: Read>(reader: &mut R) -> Result<Option<Vec<u8>>, FrameError> {
    let mut prefix = [0_u8; 4];
    let mut first = [0_u8; 1];

    loop {
        match reader.read(&mut first) {
            Ok(0) => return Ok(None),
            Ok(1) => break,
            Ok(_) => unreachable!(),
            Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
            Err(error) => return Err(error.into()),
        }
    }

    prefix[0] = first[0];
    reader.read_exact(&mut prefix[1..])?;
    let length = u32::from_le_bytes(prefix) as usize;

    if length == 0 {
        return Err(FrameError::EmptyFrame);
    }

    if length > MAX_FRAME_BYTES {
        return Err(FrameError::FrameTooLarge {
            actual: length,
            maximum: MAX_FRAME_BYTES,
        });
    }

    let mut payload = vec![0_u8; length];
    reader.read_exact(&mut payload)?;
    Ok(Some(payload))
}

pub fn read_envelope<R: Read>(reader: &mut R) -> Result<Option<Envelope>, FrameError> {
    read_frame(reader)?
        .map(|payload| serde_json::from_slice(&payload).map_err(FrameError::from))
        .transpose()
}

pub fn write_envelope<W: Write>(writer: &mut W, envelope: &Envelope) -> Result<(), FrameError> {
    let payload = serde_json::to_vec(envelope)?;
    if payload.is_empty() {
        return Err(FrameError::EmptyFrame);
    }
    if payload.len() > MAX_FRAME_BYTES {
        return Err(FrameError::FrameTooLarge {
            actual: payload.len(),
            maximum: MAX_FRAME_BYTES,
        });
    }

    writer.write_all(&(payload.len() as u32).to_le_bytes())?;
    writer.write_all(&payload)?;
    writer.flush()?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::io::Cursor;

    #[test]
    fn frame_round_trip_preserves_the_envelope() {
        let expected = Envelope::request("hello", "request-1", json!({ "client": "test" }));
        let mut bytes = Vec::new();
        write_envelope(&mut bytes, &expected).unwrap();

        let actual = read_envelope(&mut Cursor::new(bytes)).unwrap().unwrap();
        assert_eq!(actual, expected);
    }

    #[test]
    fn clean_end_of_stream_is_not_an_error() {
        assert!(read_frame(&mut Cursor::new(Vec::<u8>::new()))
            .unwrap()
            .is_none());
    }

    #[test]
    fn oversized_frames_are_rejected_before_allocation() {
        let prefix = ((MAX_FRAME_BYTES + 1) as u32).to_le_bytes();
        let error = read_frame(&mut Cursor::new(prefix)).unwrap_err();
        assert!(matches!(error, FrameError::FrameTooLarge { .. }));
    }

    #[test]
    fn zero_length_frames_are_rejected() {
        let error = read_frame(&mut Cursor::new(0_u32.to_le_bytes())).unwrap_err();
        assert!(matches!(error, FrameError::EmptyFrame));
    }

    #[test]
    fn truncated_length_prefixes_are_io_errors() {
        let error = read_frame(&mut Cursor::new(vec![1_u8, 0_u8])).unwrap_err();
        assert!(
            matches!(error, FrameError::Io(ref inner) if inner.kind() == io::ErrorKind::UnexpectedEof)
        );
    }

    #[test]
    fn truncated_payloads_are_io_errors() {
        let mut bytes = 3_u32.to_le_bytes().to_vec();
        bytes.extend_from_slice(b"{}");
        let error = read_frame(&mut Cursor::new(bytes)).unwrap_err();
        assert!(
            matches!(error, FrameError::Io(ref inner) if inner.kind() == io::ErrorKind::UnexpectedEof)
        );
    }

    #[test]
    fn invalid_json_frames_are_rejected() {
        let mut bytes = 2_u32.to_le_bytes().to_vec();
        bytes.extend_from_slice(b"xx");
        let error = read_envelope(&mut Cursor::new(bytes)).unwrap_err();
        assert!(matches!(error, FrameError::Json(_)));
    }

    #[test]
    fn oversized_envelopes_are_rejected_before_writing() {
        let envelope = Envelope::event(
            "large",
            None,
            json!({ "data": "x".repeat(MAX_FRAME_BYTES) }),
        );
        let mut bytes = Vec::new();
        let error = write_envelope(&mut bytes, &envelope).unwrap_err();
        assert!(matches!(error, FrameError::FrameTooLarge { .. }));
        assert!(bytes.is_empty());
    }
}
