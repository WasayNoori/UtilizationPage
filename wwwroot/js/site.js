// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

function loadAverageRating() {
    const requestUrl = window.location.pathname + '?handler=AverageRating';
    
    $.ajax({
        url: requestUrl,
        method: 'GET',
        success: function(response) {
            if (response && response.success) {
                const rating = response.averageRating;
                const fullStars = Math.floor(rating);
                const hasHalfStar = rating % 1 >= 0.5;
                
                let starsHtml = '';
                for (let i = 0; i < 5; i++) {
                    if (i < fullStars) {
                        starsHtml += '<i class="fas fa-star text-warning"></i>';
                    } else if (i === fullStars && hasHalfStar) {
                        starsHtml += '<i class="fas fa-star-half-alt text-warning"></i>';
                    } else {
                        starsHtml += '<i class="far fa-star text-warning"></i>';
                    }
                }
                starsHtml += ` <span class="ms-2">(${rating.toFixed(1)})</span>`;
                
                $('#averageRating').html(starsHtml);
            } else {
                $('#averageRating').html('<span class="text-muted">No ratings yet</span>');
            }
        },
        error: function() {
            $('#averageRating').html('<span class="text-danger">Error loading rating</span>');
        }
    });
}

function loadReviews() {
    console.log('Loading reviews...');
    const requestUrl = window.location.pathname + '?handler=Reviews';
    console.log('Request URL:', requestUrl);
    
    // Show loading spinner
    document.getElementById('reviewsContainer').innerHTML = `
        <div class="text-center">
            <div class="spinner-border spinner-border-sm" role="status">
                <span class="visually-hidden">Loading reviews...</span>
            </div>
        </div>`;

    // Get the antiforgery token
    const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
    console.log('Antiforgery token found:', !!token);

    fetch(requestUrl, {
        method: 'GET',
        headers: {
            'Content-Type': 'application/json',
            'RequestVerificationToken': token || ''
        },
        credentials: 'same-origin'
    })
    .then(response => {
        console.log('Response status:', response.status);
        console.log('Response headers:', [...response.headers.entries()]);
        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }
        return response.json();
    })
    .then(result => {
        console.log('Reviews data:', result);
        if (!result.success) {
            throw new Error(result.error || 'Failed to load reviews');
        }
        
        const reviews = result.data;
        console.log('Number of reviews:', reviews?.length || 0);
        const container = document.getElementById('reviewsContainer');
        
        if (!reviews || reviews.length === 0) {
            container.innerHTML = '<div class="no-reviews">No reviews yet</div>';
            return;
        }

        container.innerHTML = reviews.map(review => `
            <div class="review-item">
                <div class="review-stars text-warning mb-2">
                    ${'★'.repeat(review.stars)}${'☆'.repeat(5 - review.stars)}
                </div>
                ${review.comments ? `<div class="review-comment">${review.comments}</div>` : ''}
            </div>
        `).join('');
    })
    .catch(error => {
        console.error('Error fetching reviews:', error);
        console.error('Error details:', {
            name: error.name,
            message: error.message,
            stack: error.stack
        });
        document.getElementById('reviewsContainer').innerHTML = 
            '<div class="no-reviews text-danger">Error loading reviews</div>';
    });
}

// Add feedback tab event listener
document.getElementById('feedback-tab').addEventListener('click', function (e) {
    console.log('Feedback tab clicked');
    loadReviews();
    loadAverageRating();
});

// Initialize when the document is ready
document.addEventListener('DOMContentLoaded', function() {
    console.log('Document ready, initializing...');
    
    // Add tab event listeners
    document.getElementById('feedback-tab').addEventListener('shown.bs.tab', function (e) {
        console.log('Feedback tab shown');
        loadReviews();
        loadAverageRating();
    });

    // Initialize star rating functionality
    const stars = document.querySelectorAll('.star-rating .star');
    let selectedRating = 0;

    stars.forEach((star, index) => {
        star.addEventListener('mouseover', () => {
            stars.forEach((s, i) => {
                s.classList.toggle('active', i <= index);
            });
        });

        star.addEventListener('mouseout', () => {
            stars.forEach((s, i) => {
                s.classList.toggle('active', i < selectedRating);
            });
        });

        star.addEventListener('click', () => {
            selectedRating = index + 1;
            stars.forEach((s, i) => {
                s.classList.toggle('active', i < selectedRating);
            });
        });
    });

    // Add form submission handler
    const form = document.getElementById('feedbackForm');
    if (form) {
        form.addEventListener('submit', function(e) {
            e.preventDefault();
            console.log('Form submitted');
            
            const rating = document.querySelector('input[name="rating"]:checked');
            const comment = document.getElementById('comment').value;
            const ratingError = document.getElementById('ratingError');
            
            if (!rating) {
                ratingError.classList.remove('d-none');
                return;
            }
            
            ratingError.classList.add('d-none');
            
            fetch('?handler=AddReviewAsync', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'RequestVerificationToken': document.querySelector('input[name="__RequestVerificationToken"]').value
                },
                body: JSON.stringify({
                    stars: parseInt(rating.value),
                    comments: comment
                })
            })
            .then(response => response.json())
            .then(result => {
                if (result.success) {
                    // Show success message
                    showSuccess('Thank you for your feedback!');
                    
                    // Reset form
                    form.reset();
                    
                    // Refresh reviews and average rating
                    loadReviews();
                    loadAverageRating();
                } else {
                    showError('Failed to submit feedback. Please try again.');
                }
            })
            .catch(error => {
                console.error('Error:', error);
                showError('There was an error submitting your feedback. Please try again.');
            });
        });
    }
});

function showSuccess(message) {
    $('#feedbackAlert')
        .removeClass('alert-danger')
        .addClass('alert-success')
        .text(message)
        .show()
        .delay(3000)
        .fadeOut();
}

function showError(message) {
    $('#feedbackAlert')
        .removeClass('alert-success')
        .addClass('alert-danger')
        .text(message)
        .show();
}
