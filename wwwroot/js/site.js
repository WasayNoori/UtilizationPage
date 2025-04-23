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

// Add to document ready function
$(document).ready(function() {
    // ... existing code ...
    
    // Load average rating when feedback tab is shown
    $('#feedback-tab').on('shown.bs.tab', function() {
        loadAverageRating();
    });
    
    // Reload average rating after successful feedback submission
    $('#feedbackForm').on('submit', function(e) {
        e.preventDefault();
        
        let rating = $('#selectedRating').val();
        let comment = $('#feedbackComment').val();

        if (!rating) {
            showError('Please select a rating');
            return;
        }

        $.ajax({
            url: window.location.pathname + '?handler=AddReview',
            type: 'POST',
            headers: {
                'RequestVerificationToken': $('input[name="__RequestVerificationToken"]').val()
            },
            data: JSON.stringify({
                stars: parseInt(rating),
                comments: comment
            }),
            contentType: 'application/json',
            success: function(response) {
                if (response.success) {
                    showSuccess('Thank you for your feedback!');
                    resetForm();
                    loadAverageRating(); // Reload the average rating
                } else {
                    showError('Failed to submit feedback. Please try again.');
                }
            },
            error: function() {
                showError('An error occurred. Please try again later.');
            }
        });
    });
});

// Add these helper functions if they don't exist
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

function resetForm() {
    $('#selectedRating').val('');
    $('#feedbackComment').val('');
    highlightStars(0);
    $('#feedbackModal').modal('hide');
}

function highlightStars(rating) {
    $('.star-rating i').each(function() {
        let starRating = $(this).data('rating');
        $(this).toggleClass('text-warning', starRating <= rating);
    });
}
